namespace Intelligently.Core.Agents.Tools;

/// <summary>Executes one tool. Implementations live in the Execution Plane and never decide their own permission.</summary>
public interface IToolHandler
{
    ToolDescriptor Descriptor { get; }
    Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default);
}
