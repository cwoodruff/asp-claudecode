using System.Diagnostics;
using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Intelligently.Infrastructure.Agents.StateGraph;

/// <summary>
/// Drives one run through the 7-stage conditional state graph one stage at a time.
/// Every call executes exactly one stage handler, validates the returned edge
/// against <see cref="StageTransitions"/>, and commits, so a crash or restart
/// resumes from the last committed stage. The whole stage runs inside one database
/// transaction: rows a handler or an exclusive tool saves mid-stage (an escalation, an
/// idempotency receipt) become visible together with the transition, never without it.
/// </summary>
public sealed class StateGraphRunner
{
    private readonly IntelligentlyDbContext _db;
    private readonly IReadOnlyDictionary<AgentStage, IStageHandler> _handlers;
    private readonly IEvidenceSink _evidence;
    private readonly ILogger<StateGraphRunner> _logger;

    public StateGraphRunner(
        IntelligentlyDbContext db,
        IEnumerable<IStageHandler> handlers,
        IEvidenceSink evidence,
        ILogger<StateGraphRunner> logger)
    {
        _db = db;
        _handlers = handlers.ToDictionary(h => h.Stage);
        _evidence = evidence;
        _logger = logger;
    }

    /// <summary>
    /// Executes the handler for the run's current stage and commits the transition.
    /// Returns the run as persisted. Runs that are not <c>Running</c> are returned untouched.
    /// </summary>
    public async Task<AgentRun> StepAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId, ct)
                  ?? throw new KeyNotFoundException($"Agent run {runId} was not found.");

        if (!run.IsActive)
            return run;

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
        return run;
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

    private async Task CommitAsync(AgentRun run, IDbContextTransaction transaction, CancellationToken ct)
    {
        run.ConcurrencyStamp = Guid.NewGuid();
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
