namespace Intelligently.Core.Agents.Tools;

/// <summary>
/// A provider of tool handlers discovered at runtime rather than registered one by one (for example
/// the tools of a connected MCP server). The registry merges every source's handlers with the native ones.
/// </summary>
public interface IToolSource
{
    /// <summary>The handlers discovered so far; empty until the source has connected.</summary>
    IReadOnlyList<IToolHandler> Handlers { get; }
}
