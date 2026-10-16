using Intelligently.Core.Agents.Contracts;
using Microsoft.Extensions.Logging;

namespace Intelligently.Infrastructure.Agents.StateGraph;

/// <summary>
/// Queue for a host without a <c>RunWorker</c> (Admin). Enqueuing never blocks and never stores
/// anything: the Web worker's periodic sweep picks up runs decided here (Running at AwaitingApproval),
/// so an in-process queue that nobody drains would only fill up and stall the 101st decision.
/// </summary>
public sealed class NoWorkerRunQueue : IRunQueue
{
    private readonly ILogger<NoWorkerRunQueue> _logger;

    public NoWorkerRunQueue(ILogger<NoWorkerRunQueue> logger) => _logger = logger;

    public ValueTask EnqueueAsync(Guid runId, CancellationToken ct = default)
    {
        _logger.LogDebug("Run {RunId} enqueued in a host without a worker; the Web worker's sweep will pick it up", runId);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<Guid> DequeueAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
