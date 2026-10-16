using Intelligently.Core.Agents.Tools;

namespace Intelligently.Infrastructure.Agents.Execution;

/// <summary>In-memory registry built from the registered <see cref="IToolHandler"/>s, sorted by name.</summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly SortedDictionary<string, IToolHandler> _handlers = new(StringComparer.Ordinal);

    public ToolRegistry(IEnumerable<IToolHandler> handlers)
    {
        foreach (var handler in handlers)
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
