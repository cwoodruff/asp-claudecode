namespace Intelligently.Core.Agents.Contracts;

/// <summary>Bounded queue of run ids awaiting a worker.</summary>
public interface IRunQueue
{
    ValueTask EnqueueAsync(Guid runId, CancellationToken ct = default);
    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct = default);
}
