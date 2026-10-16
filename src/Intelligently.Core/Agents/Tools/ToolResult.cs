namespace Intelligently.Core.Agents.Tools;

/// <summary>
/// Normalized tool outcome. A failed tool is reported with <see cref="IsError"/> rather than dropped.
/// <see cref="ErrorKind"/> tells Verify whether a failure is worth retrying (tool failure, rate limit)
/// or final for that call (policy denial, which forces a replan).
/// </summary>
public sealed record ToolResult(string ToolCallId, string Name, bool IsError, string Content, string? ErrorKind = null)
{
    public const string ErrorKindToolFailure = "ToolFailure";
    public const string ErrorKindPolicyDenied = "PolicyDenied";
    public const string ErrorKindRateLimited = "RateLimited";
    public const string ErrorKindBudgetExhausted = "BudgetExhausted";

    public static ToolResult Ok(ToolCall call, string content) => new(call.Id, call.Name, false, content);
    public static ToolResult Error(ToolCall call, string message) => new(call.Id, call.Name, true, message, ErrorKindToolFailure);
    public static ToolResult Denied(ToolCall call, string reason) => new(call.Id, call.Name, true, $"Denied by policy: {reason}", ErrorKindPolicyDenied);
    public static ToolResult RateLimited(ToolCall call, string reason) => new(call.Id, call.Name, true, $"Rate limited: {reason}", ErrorKindRateLimited);
    public static ToolResult BudgetExhausted(ToolCall call) => new(call.Id, call.Name, true, "Tool call budget exhausted.", ErrorKindBudgetExhausted);

    public bool IsPolicyDenial => IsError && ErrorKind == ErrorKindPolicyDenied;
}
