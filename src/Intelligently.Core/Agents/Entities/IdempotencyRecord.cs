namespace Intelligently.Core.Agents.Entities;

/// <summary>
/// One executed, idempotency-covered tool call. Unique on (RunId, ToolName, ArgsHash); a replay
/// with the same key returns <see cref="ResultJson"/> instead of running the tool again.
/// </summary>
public sealed class IdempotencyRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public string ToolName { get; set; } = string.Empty;

    /// <summary>SHA-256 hex of the canonical argument JSON.</summary>
    public string ArgsHash { get; set; } = string.Empty;

    /// <summary>Id of the tool call that first executed.</summary>
    public string ToolCallId { get; set; } = string.Empty;

    /// <summary>The <c>ToolResult</c> as JSON.</summary>
    public string ResultJson { get; set; } = "{}";
    public bool IsError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AgentRun Run { get; set; } = null!;
}
