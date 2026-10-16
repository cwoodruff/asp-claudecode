namespace Intelligently.Core.Agents.Tools;

/// <summary>Registry of tools available to the agent, sorted deterministically by name.</summary>
public interface IToolRegistry
{
    IReadOnlyList<ToolDescriptor> Descriptors { get; }
    bool TryGetHandler(string name, out IToolHandler handler);
}
