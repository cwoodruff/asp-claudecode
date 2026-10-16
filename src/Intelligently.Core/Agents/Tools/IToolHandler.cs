namespace Intelligently.Core.Agents.Tools;

/// <summary>
/// Executes one tool. Implementations live in the Execution Plane and never decide their own permission.
/// Handlers may run concurrently with the other calls of the same assistant turn, so a handler that
/// needs scoped services (the DbContext) must open its own scope rather than share the dispatcher's.
/// </summary>
public interface IToolHandler
{
    ToolDescriptor Descriptor { get; }
    Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct = default);
}
