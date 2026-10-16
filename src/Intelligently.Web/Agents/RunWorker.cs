using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Agents.StateGraph;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intelligently.Web.Agents;

/// <summary>
/// Execution host (plan section 2.5): drains the run queue and steps each run
/// through the state graph until it is Waiting, Completed or Failed. Every stage
/// runs in its own DI scope so each one is committed independently and a crash
/// resumes from the last committed stage.
/// </summary>
/// <remarks>
/// The queue is in-process, so two things would otherwise be lost: runs that were Running when
/// the process died, and runs whose approval was decided in the Admin process (which has no
/// worker). A sweep task re-enqueues every Running run at startup and, every
/// <see cref="AgentsOptions.ApprovalSweepSeconds"/>, every run that is Running at AwaitingApproval
/// (that is, decided but not yet picked up) or whose stage lease has expired (abandoned by a
/// crashed worker). Enqueuing a run twice, here or on another instance, is harmless: every stage
/// runs under a database lease taken as this worker's <see cref="IWorkerIdentity"/>, so a duplicate
/// simply fails to claim the run and is dropped, and a run that is no longer Running is returned untouched.
/// </remarks>
public sealed class RunWorker : BackgroundService
{
    /// <summary>Safety valve on top of the recovery budget; a healthy run needs far fewer stages.</summary>
    private const int MaxStagesPerDequeue = 500;

    private readonly IRunQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly IWorkerIdentity _identity;
    private readonly AgentsOptions _options;
    private readonly ILogger<RunWorker> _logger;

    public RunWorker(IRunQueue queue, IServiceScopeFactory scopes, IWorkerIdentity identity, IOptions<AgentsOptions> options, ILogger<RunWorker> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _identity = identity;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Recorded as <c>AgentRun.ClaimedBy</c> on every stage this worker executes.</summary>
    public string WorkerId => _identity.Id;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RunWorker {WorkerId} started", WorkerId);
        var sweep = SweepAsync(stoppingToken);
        try
        {
            await foreach (var runId in _queue.DequeueAllAsync(stoppingToken))
                await ProcessAsync(runId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        await sweep;
        _logger.LogInformation("RunWorker stopped");
    }

    internal async Task ProcessAsync(Guid runId, CancellationToken ct)
    {
        for (var stages = 0; stages < MaxStagesPerDequeue; stages++)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                using var scope = _scopes.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<StateGraphRunner>();
                var (run, executed) = await runner.TryStepAsync(runId, WorkerId, ct);
                if (!run.IsActive)
                {
                    _logger.LogInformation("Run {RunId} left the worker at {Stage} ({Status})", runId, run.Stage, run.Status);
                    return;
                }
                if (!executed)
                {
                    // Another instance holds the lease (or a duplicate enqueue raced us); it will carry the run on.
                    _logger.LogDebug("Run {RunId} is held by {Holder} until {Until:O}; dropping it here", runId, run.ClaimedBy, run.ClaimedUntil);
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The runner has already persisted the failure; nothing more to do for this run.
                _logger.LogError(ex, "Run {RunId} stopped after an error", runId);
                return;
            }
        }

        _logger.LogWarning("Run {RunId} exceeded {Max} stages in one dequeue; parking it", runId, MaxStagesPerDequeue);
    }

    /// <summary>Startup resume of every Running run, then a periodic sweep for decided approvals and expired leases.</summary>
    private async Task SweepAsync(CancellationToken ct)
    {
        await Task.Yield();
        await EnqueueAsync(r => r.Status == AgentRun.StatusRunning, "startup resume", ct);

        var interval = TimeSpan.FromSeconds(_options.ApprovalSweepSeconds);
        if (interval <= TimeSpan.Zero) return;

        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = DateTime.UtcNow;
                await EnqueueAsync(r => r.Status == AgentRun.StatusRunning
                                        && (r.Stage == AgentStage.AwaitingApproval
                                            || (r.ClaimedUntil != null && r.ClaimedUntil < now)), "approval and lease sweep", ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task EnqueueAsync(System.Linq.Expressions.Expression<Func<AgentRun, bool>> filter, string why, CancellationToken ct)
    {
        List<Guid> ids;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IntelligentlyDbContext>();
            ids = await db.AgentRuns.AsNoTracking().Where(filter).Select(r => r.Id).ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RunWorker {Why} could not query runs; will try again", why);
            return;
        }

        foreach (var id in ids)
            await _queue.EnqueueAsync(id, ct);

        if (ids.Count > 0)
            _logger.LogInformation("RunWorker {Why}: re-enqueued {Count} run(s)", why, ids.Count);
    }
}
