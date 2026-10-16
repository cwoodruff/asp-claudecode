using Intelligently.Core.Agents.StateGraph;

namespace Intelligently.Core.Agents.Entities;

/// <summary>
/// Append-only evidence row. Rows are never updated; corrections are new rows.
/// </summary>
public sealed class AgentTrace
{
    public const string KindTransition = "Transition";
    public const string KindModelCall = "ModelCall";
    public const string KindToolCall = "ToolCall";
    public const string KindEscalation = "Escalation";
    public const string KindApproval = "Approval";
    public const string KindNote = "Note";

    public long Id { get; set; }
    public Guid RunId { get; set; }
    public Guid? StepId { get; set; }
    public AgentStage Stage { get; set; }

    /// <summary>Transition | ModelCall | ToolCall | Escalation | Approval | Note.</summary>
    public string Kind { get; set; } = KindNote;

    /// <summary>JSON payload: prompt envelope, raw response, tool payload, etc.</summary>
    public string Payload { get; set; } = "{}";

    public string? Model { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadInputTokens { get; set; }
    public int CacheCreationInputTokens { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AgentRun Run { get; set; } = null!;
}
