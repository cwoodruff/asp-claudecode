using Intelligently.Core.Agents.StateGraph;

namespace Intelligently.Core.Agents.Entities;

/// <summary>
/// Append-only evidence row (plan section 1.5). Rows are never updated or deleted; corrections are new
/// rows, and the DbContext rejects any other change to a row in SaveChanges.
/// </summary>
public sealed class AgentTrace
{
    public const string KindTransition = "Transition";
    public const string KindModelCall = "ModelCall";
    public const string KindToolCall = "ToolCall";
    public const string KindSandboxLog = "SandboxLog";
    public const string KindEscalation = "Escalation";
    public const string KindApproval = "Approval";
    public const string KindNote = "Note";
    /// <summary>A model judgement of a step (Verify): score, pass/fail and rationale.</summary>
    public const string KindJudgement = "Judgement";
    /// <summary>One plan candidate of a candidate search (Plan): the plan, its score and whether it was kept.</summary>
    public const string KindCandidate = "Candidate";

    public long Id { get; set; }
    public Guid RunId { get; set; }
    public Guid? StepId { get; set; }
    public AgentStage Stage { get; set; }

    /// <summary>Transition | ModelCall | ToolCall | SandboxLog | Escalation | Approval | Note | Judgement | Candidate.</summary>
    public string Kind { get; set; } = KindNote;

    /// <summary>JSON payload: the exact prompt envelope and raw response of a model call, a tool call with its payload, sandbox logs, etc.</summary>
    public string Payload { get; set; } = "{}";

    public string? Model { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadInputTokens { get; set; }
    public int CacheCreationInputTokens { get; set; }

    /// <summary>USD charged for this row's usage by the cost accountant (model calls only).</summary>
    public decimal CostUsd { get; set; }

    /// <summary>The provider's reasoning summary (<c>thinking.display = summarized</c>) as text, for model calls.</summary>
    public string? ReasoningSummary { get; set; }

    /// <summary>Stop reason of a model call (<c>end_turn</c>, <c>tool_use</c>, <c>max_tokens</c>, ...).</summary>
    public string? StopReason { get; set; }

    /// <summary>Effort the model call was made at (<c>low</c>, <c>medium</c>, <c>high</c>, ...).</summary>
    public string? Effort { get; set; }

    /// <summary>Wall-clock latency of the model or tool call in milliseconds.</summary>
    public double? LatencyMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AgentRun Run { get; set; } = null!;
}
