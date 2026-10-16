using Intelligently.Core.Agents.Tools;

namespace Intelligently.Infrastructure.Agents.Execution;

/// <summary>
/// In-memory registry built from the registered <see cref="IToolHandler"/>s plus the handlers of every
/// <see cref="IToolSource"/> (MCP servers), sorted by name so the tool list is deterministic.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly SortedDictionary<string, IToolHandler> _handlers = new(StringComparer.Ordinal);

    public ToolRegistry(IEnumerable<IToolHandler> handlers)
        : this(handlers, []) { }

    public ToolRegistry(IEnumerable<IToolHandler> handlers, IEnumerable<IToolSource> sources)
    {
        foreach (var handler in handlers.Concat(sources.SelectMany(s => s.Handlers)))
        {
            if (!_handlers.TryAdd(handler.Descriptor.Name, handler))
                throw new InvalidOperationException($"Duplicate tool name '{handler.Descriptor.Name}'.");
        }

        Descriptors = _handlers.Values.Select(h => h.Descriptor).ToList();
    }

    public IReadOnlyList<ToolDescriptor> Descriptors { get; }

    public bool TryGetHandler(string name, out IToolHandler handler) =>
        _handlers.TryGetValue(name, out handler!);
}
