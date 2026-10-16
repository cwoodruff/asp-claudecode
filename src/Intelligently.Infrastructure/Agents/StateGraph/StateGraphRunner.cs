using System.Diagnostics;
using System.Text.Json;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.StateGraph;

/// <summary>
/// Drives one run through the 7-stage conditional state graph one stage at a time.
/// Every call executes exactly one stage handler, validates the returned edge
/// against <see cref="StageTransitions"/>, and commits, so a crash or restart
/// resumes from the last committed stage. The whole stage runs inside one database
/// transaction: rows a handler or an exclusive tool saves mid-stage (an escalation, an
/// idempotency receipt) become visible together with the transition, never without it.
/// </summary>
/// <remarks>
/// The whole stage runs inside one database transaction: rows a handler or an exclusive tool saves
/// mid-stage (an escalation, an idempotency receipt) become visible together with the transition,
/// never without it.
/// Stage execution is exclusive across worker instances. Before the handler runs (and before the
/// stage transaction opens, so other instances see it at once) the run is claimed with a single
/// auto-committed conditional UPDATE that only succeeds while the run is Running and unclaimed
/// (or its lease has expired); a failed claim returns without executing anything.
/// The claim is released by the commit (success or failure) or, when nothing could be committed,
/// by a best-effort conditional release, and an expired lease can be taken over by any worker.
/// <c>ClaimedUntil</c> is also a concurrency token, so a worker whose lease was taken over cannot
/// commit its stale stage.
/// </remarks>
public sealed class StateGraphRunner
{
    private readonly IntelligentlyDbContext _db;
    private readonly IReadOnlyDictionary<AgentStage, IStageHandler> _handlers;
    private readonly IEvidenceSink _evidence;
    private readonly IWorkerIdentity _identity;
    private readonly TimeSpan _lease;
    private readonly ILogger<StateGraphRunner> _logger;

    public StateGraphRunner(
        IntelligentlyDbContext db,
        IEnumerable<IStageHandler> handlers,
        IEvidenceSink evidence,
        IWorkerIdentity identity,
        IOptions<AgentsOptions> options,
        ILogger<StateGraphRunner> logger)
    {
        _db = db;
        _handlers = handlers.ToDictionary(h => h.Stage);
        _evidence = evidence;
        _identity = identity;
        _lease = TimeSpan.FromSeconds(Math.Max(1, options.Value.StageLeaseSeconds));
        _logger = logger;
    }

    /// <summary>
    /// Executes the handler for the run's current stage under this worker's lease and commits the transition.
    /// Returns the run as persisted. Runs that are not <c>Running</c>, or that another worker holds, are returned untouched.
    /// </summary>
    public async Task<AgentRun> StepAsync(Guid runId, CancellationToken ct = default) =>
        (await TryStepAsync(runId, _identity.Id, ct)).Run;

    /// <summary>Same as <see cref="StepAsync"/>, claiming the run as this worker and reporting whether a stage ran.</summary>
    public Task<StepResult> TryStepAsync(Guid runId, CancellationToken ct = default) =>
        TryStepAsync(runId, _identity.Id, ct);

    /// <summary>
    /// Claims the run as <paramref name="claimedBy"/> and, when the claim succeeds, executes exactly one stage.
    /// </summary>
    public async Task<StepResult> TryStepAsync(Guid runId, string claimedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(claimedBy))
            throw new ArgumentException("The claiming worker must be named.", nameof(claimedBy));

        var now = DateTime.UtcNow;
        var until = now + _lease;

        // One conditional UPDATE, auto-committed, is the whole claim: only a Running run with no live lease matches.
        var claimed = await _db.AgentRuns
            .Where(r => r.Id == runId
                        && r.Status == AgentRun.StatusRunning
                        && (r.ClaimedUntil == null || r.ClaimedUntil < now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ClaimedBy, claimedBy)
                .SetProperty(r => r.ClaimedUntil, until), ct);

        if (claimed == 0)
        {
            var untouched = await _db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct)
                            ?? throw new KeyNotFoundException($"Agent run {runId} was not found.");
            if (untouched.IsActive)
                _logger.LogDebug("Run {RunId}: not claimed by {Worker}; {Holder} holds it until {Until:O}",
                    runId, claimedBy, untouched.ClaimedBy, untouched.ClaimedUntil);
            return new StepResult(untouched, Executed: false);
        }

        var run = await _db.AgentRuns.FirstAsync(r => r.Id == runId, ct);
        var released = false;
        try
        {
            released = await ExecuteStageAsync(run, ct);
            return new StepResult(run, Executed: true);
        }
        finally
        {
            if (!released)
                await ReleaseClaimAsync(runId, claimedBy, until);
        }
    }

    /// <summary>Runs the stage. Returns true when the run was committed (which releases the claim).</summary>
    private async Task<bool> ExecuteStageAsync(AgentRun run, CancellationToken ct)
    {
        var from = run.Stage;
        if (!_handlers.TryGetValue(from, out var handler))
            throw new InvalidOperationException($"No stage handler is registered for {from}.");

        using var span = _evidence.StartStage(run, from);
        var stopwatch = Stopwatch.StartNew();
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        StageOutcome outcome;
        try
        {
            outcome = await handler.ExecuteAsync(run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Run {RunId}: unhandled error in stage {Stage}", run.Id, from);
            await FailAsync(run, from, $"Unhandled error in {from}: {ex.Message}", transaction, ct);
            throw;
        }

        if (!StageTransitions.IsAllowed(from, outcome.Next))
        {
            var illegal = new IllegalStageTransitionException(run.Id, from, outcome.Next);
            _logger.LogError(illegal, "Run {RunId}: handler for {Stage} returned an illegal transition", run.Id, from);
            await FailAsync(run, from, illegal.Message, transaction, ct);
            throw illegal;
        }

        stopwatch.Stop();
        run.Budget.TrySpendWallClock(stopwatch.Elapsed);
        Apply(run, outcome);

        await _evidence.RecordAsync(run, new AgentTrace
        {
            Stage = from,
            Kind = AgentTrace.KindTransition,
            Payload = JsonSerializer.Serialize(new
            {
                from = from.ToString(),
                to = outcome.Next.ToString(),
                reason = outcome.Reason,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                status = run.Status,
            }),
        }, ct);

        span?.SetTag("agent.next_stage", outcome.Next.ToString());
        span?.SetTag("agent.status", run.Status);

        await CommitAsync(run, transaction, ct);

        _logger.LogInformation("Run {RunId}: {From} → {To} ({Reason})", run.Id, from, outcome.Next, outcome.Reason);
        return true;
    }

    private static void Apply(AgentRun run, StageOutcome outcome)
    {
        run.Stage = outcome.Next;
        run.StatusReason = outcome.Reason;
        run.Status = outcome.Next switch
        {
            AgentStage.Completed => AgentRun.StatusCompleted,
            AgentStage.Failed => AgentRun.StatusFailed,
            AgentStage.AwaitingApproval => AgentRun.StatusWaiting,
            _ => AgentRun.StatusRunning,
        };
        if (StageTransitions.IsTerminal(outcome.Next))
            run.CompletedAt = DateTime.UtcNow;
    }

    private async Task FailAsync(AgentRun run, AgentStage from, string reason, IDbContextTransaction transaction, CancellationToken ct)
    {
        run.Stage = AgentStage.Failed;
        run.Status = AgentRun.StatusFailed;
        run.StatusReason = reason;
        run.CompletedAt = DateTime.UtcNow;
        await _evidence.RecordAsync(run, new AgentTrace
        {
            Stage = from,
            Kind = AgentTrace.KindNote,
            Payload = JsonSerializer.Serialize(new { error = reason }),
        }, ct);
        await CommitAsync(run, transaction, ct);
    }

    /// <summary>
    /// Commits the stage and releases the lease in the same statement, guarded by both concurrency tokens,
    /// then commits the stage transaction.
    /// </summary>
    private async Task CommitAsync(AgentRun run, IDbContextTransaction transaction, CancellationToken ct)
    {
        run.ConcurrencyStamp = Guid.NewGuid();
        run.ClaimedBy = null;
        run.ClaimedUntil = null;
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Best-effort release when nothing was committed (cancellation, a lost concurrency race, a failed
    /// failure-commit). Conditional on the lease still being ours, so a lease another worker took over is left alone.
    /// </summary>
    private async Task ReleaseClaimAsync(Guid runId, string claimedBy, DateTime until)
    {
        try
        {
            await _db.AgentRuns
                .Where(r => r.Id == runId && r.ClaimedBy == claimedBy && r.ClaimedUntil == until)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.ClaimedBy, (string?)null)
                    .SetProperty(r => r.ClaimedUntil, (DateTime?)null), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId}: could not release the lease held by {Worker}; it expires at {Until:O}", runId, claimedBy, until);
        }
    }
}
