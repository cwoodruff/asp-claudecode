namespace Intelligently.Core.Agents.Tools;

/// <summary>
/// Executes one tool. Implementations live in the Execution Plane and never decide their own permission.
/// Handlers run concurrently with the other calls of the same assistant turn unless their descriptor is
/// <see cref="ToolDescriptor.Exclusive"/>, which is what a handler that writes through the stage's
/// DbContext must declare.
/// </summary>
public interface IToolHandler
{
    ToolDescriptor Descriptor { get; }
    Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct = default);
}
