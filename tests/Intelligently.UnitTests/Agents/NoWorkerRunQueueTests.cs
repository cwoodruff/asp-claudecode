using FluentAssertions;
using Intelligently.Infrastructure.Agents.StateGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace Intelligently.UnitTests.Agents;

/// <summary>Admin has no worker, so its queue must never fill up or block a decision.</summary>
public class NoWorkerRunQueueTests
{
    [Fact]
    public async Task Enqueuing_far_beyond_the_bounded_capacity_never_blocks()
    {
        var queue = new NoWorkerRunQueue(NullLogger<NoWorkerRunQueue>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 1_000; i++)
            await queue.EnqueueAsync(Guid.NewGuid(), cts.Token);

        cts.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task Dequeue_yields_nothing()
    {
        var queue = new NoWorkerRunQueue(NullLogger<NoWorkerRunQueue>.Instance);
        await queue.EnqueueAsync(Guid.NewGuid());

        var items = new List<Guid>();
        await foreach (var id in queue.DequeueAllAsync())
            items.Add(id);

        items.Should().BeEmpty();
    }
}
