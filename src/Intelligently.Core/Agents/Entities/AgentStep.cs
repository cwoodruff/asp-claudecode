using Intelligently.Core.Agents.StateGraph;

namespace Intelligently.Core.Agents.Entities;

/// <summary>One attempt at executing a plan step (an Act, Observe, Verify pass).</summary>
public sealed class AgentStep
{
    public const string StatusRunning = "Running";
    public const string StatusObserved = "Observed";
    public const string StatusPassed = "Passed";
    public const string StatusFailed = "Failed";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public int PlanRevision { get; set; }
    public int PlanStepIndex { get; set; }

    /// <summary>1-based attempt number for this plan step.</summary>
    public int Attempt { get; set; } = 1;

    public AgentStage Stage { get; set; } = AgentStage.Act;
    public string Status { get; set; } = StatusRunning;

    /// <summary>Assistant text returned alongside (or instead of) tool calls.</summary>
    public string? AssistantText { get; set; }

    /// <summary>JSON array of the tool calls the model proposed.</summary>
    public string? ToolCallsJson { get; set; }

    /// <summary>JSON array of normalized tool results captured by Observe.</summary>
    public string? ToolResultsJson { get; set; }

    /// <summary>Verifier verdict message.</summary>
    public string? VerifierMessage { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public AgentRun Run { get; set; } = null!;
}
