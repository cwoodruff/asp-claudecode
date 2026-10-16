using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>
/// Result of <see cref="IReasoningEngine.PlanAsync"/>. <see cref="ContentJson"/> is the JSON array of the
/// response content blocks exactly as the provider returned them, so the transcript can replay them.
/// <see cref="Metadata"/> carries the reasoning summary, stop reason, effort and latency for the evidence row.
/// </summary>
public sealed record PlanResult(AgentPlan? Plan, ModelUsage Usage, string RawResponse, string? FailureReason = null, string? ContentJson = null, ModelCallMetadata? Metadata = null)
{
    public bool Succeeded => Plan is not null;
}

/// <summary>
/// Result of <see cref="IReasoningEngine.DecideAsync"/>. <see cref="ContentJson"/> is the JSON array of the
/// response content blocks exactly as the provider returned them (text, tool use, thinking, compaction),
/// appended back to the transcript on later turns so signed blocks survive.
/// </summary>
public sealed record DecisionResult(
    IReadOnlyList<ToolCall> ToolCalls,
    string? AssistantText,
    string StopReason,
    ModelUsage Usage,
    string RawResponse,
    string? ContentJson = null,
    ModelCallMetadata? Metadata = null);

/// <summary>Result of <see cref="IReasoningEngine.JudgeAsync"/>.</summary>
public sealed record JudgementResult(bool Passed, double Score, string Rationale, ModelUsage Usage, string RawResponse, ModelCallMetadata? Metadata = null);

/// <summary>
/// Intelligence Plane: turns intent into structured plans and candidate tool calls.
/// </summary>
public interface IReasoningEngine
{
    /// <summary>Produces a schema-valid <see cref="AgentPlan"/> via structured output.</summary>
    Task<PlanResult> PlanAsync(PromptEnvelope envelope, CancellationToken ct = default);

    /// <summary>One tool-use turn: returns zero or more tool calls the model wants to make.</summary>
    Task<DecisionResult> DecideAsync(PromptEnvelope envelope, CancellationToken ct = default);

    /// <summary>Scores a candidate against a rubric. Only used when deterministic verification cannot decide.</summary>
    Task<JudgementResult> JudgeAsync(PromptEnvelope envelope, string rubric, CancellationToken ct = default);
}
