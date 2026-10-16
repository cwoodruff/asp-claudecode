using System.Text.Json;

namespace Intelligently.Core.Agents.Tools;

/// <summary>
/// Normalized tool outcome. A failed tool is reported with <see cref="IsError"/> rather than dropped.
/// <see cref="ErrorKind"/> tells Verify whether a failure is worth retrying (tool failure, rate limit,
/// invalid output) or final for that call (policy denial, which forces a replan). <see cref="Content"/>
/// is the text the model sees; <see cref="Payload"/> is the structured form of the same result when the
/// tool produced one (MCP structured content, native tool output records), used for schema validation.
/// </summary>
public sealed record ToolResult(string ToolCallId, string Name, bool IsError, string Content, string? ErrorKind = null, JsonElement? Payload = null)
{
    public const string ErrorKindToolFailure = "ToolFailure";
    public const string ErrorKindPolicyDenied = "PolicyDenied";
    public const string ErrorKindRateLimited = "RateLimited";
    public const string ErrorKindBudgetExhausted = "BudgetExhausted";
    public const string ErrorKindOutputInvalid = "OutputInvalid";

    public static ToolResult Ok(ToolCall call, string content) => new(call.Id, call.Name, false, content);
    public static ToolResult Ok(ToolCall call, JsonElement payload) => new(call.Id, call.Name, false, payload.GetRawText(), null, payload.Clone());
    public static ToolResult Error(ToolCall call, string message) => new(call.Id, call.Name, true, message, ErrorKindToolFailure);
    public static ToolResult Denied(ToolCall call, string reason) => new(call.Id, call.Name, true, $"Denied by policy: {reason}", ErrorKindPolicyDenied);
    public static ToolResult RateLimited(ToolCall call, string reason) => new(call.Id, call.Name, true, $"Rate limited: {reason}", ErrorKindRateLimited);
    public static ToolResult BudgetExhausted(ToolCall call) => new(call.Id, call.Name, true, "Tool call budget exhausted.", ErrorKindBudgetExhausted);
    public static ToolResult OutputInvalid(ToolCall call, string reason) => new(call.Id, call.Name, true, $"Tool '{call.Name}' returned output that violates its declared schema: {reason}", ErrorKindOutputInvalid);

    public bool IsPolicyDenial => IsError && ErrorKind == ErrorKindPolicyDenied;
}
