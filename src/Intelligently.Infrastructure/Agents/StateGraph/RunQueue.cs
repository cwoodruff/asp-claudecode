using System.Threading.Channels;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.StateGraph;

/// <summary>Bounded in-process queue of run ids (plan section 2.5).</summary>
public sealed class RunQueue : IRunQueue
{
    private readonly Channel<Guid> _channel;

    public RunQueue(IOptions<AgentsOptions> options)
        : this(Math.Max(1, options.Value.QueueCapacity)) { }

    public RunQueue(int capacity)
    {
        _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public ValueTask EnqueueAsync(Guid runId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(runId, ct);

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);
}
