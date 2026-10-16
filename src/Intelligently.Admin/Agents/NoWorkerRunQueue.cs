using Intelligently.Core.Agents.Contracts;

namespace Intelligently.Admin.Agents;

/// <summary>
/// The <see cref="IRunQueue"/> for a host without a <c>RunWorker</c>. Admin never steps runs; after an
/// approval decision the run is already saved as Running at AwaitingApproval, and the Web worker's
/// approval sweep (<c>Agents:ApprovalSweepSeconds</c>) re-enqueues it there. Enqueueing here is a no-op
/// instead of a write into a bounded channel nobody reads, which would block once it filled up.
/// </summary>
public sealed class NoWorkerRunQueue : IRunQueue
{
    private readonly ILogger<NoWorkerRunQueue> _logger;

    public NoWorkerRunQueue(ILogger<NoWorkerRunQueue> logger)
    {
        _logger = logger;
    }

    public ValueTask EnqueueAsync(Guid runId, CancellationToken ct = default)
    {
        _logger.LogDebug("Run {RunId} resumed; the Web worker's approval sweep will pick it up", runId);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct = default) => AsyncEnumerable.Empty<Guid>();
}
