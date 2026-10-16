using Intelligently.Core.Agents.Contracts;
using Intelligently.Infrastructure.Agents.StateGraph;

namespace Intelligently.Web.Agents;

/// <summary>
/// Execution host (plan section 2.5): drains the run queue and steps each run
/// through the state graph until it is Waiting, Completed or Failed. Every stage
/// runs in its own DI scope so each one is committed independently and a crash
/// resumes from the last committed stage.
/// </summary>
public sealed class RunWorker : BackgroundService
{
    /// <summary>Safety valve on top of the recovery budget; a healthy run needs far fewer stages.</summary>
    private const int MaxStagesPerDequeue = 500;

    private readonly IRunQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RunWorker> _logger;

    public RunWorker(IRunQueue queue, IServiceScopeFactory scopes, ILogger<RunWorker> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RunWorker started");
        try
        {
            await foreach (var runId in _queue.DequeueAllAsync(stoppingToken))
                await ProcessAsync(runId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
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
                var run = await runner.StepAsync(runId, ct);
                if (!run.IsActive)
                {
                    _logger.LogInformation("Run {RunId} left the worker at {Stage} ({Status})", runId, run.Stage, run.Status);
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
}
