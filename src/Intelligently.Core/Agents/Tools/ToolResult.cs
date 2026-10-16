namespace Intelligently.Core.Agents.Tools;

/// <summary>Normalized tool outcome. A failed tool is reported with <see cref="IsError"/> rather than dropped.</summary>
public sealed record ToolResult(string ToolCallId, string Name, bool IsError, string Content)
{
    public static ToolResult Ok(ToolCall call, string content) => new(call.Id, call.Name, false, content);
    public static ToolResult Error(ToolCall call, string message) => new(call.Id, call.Name, true, message);
}
