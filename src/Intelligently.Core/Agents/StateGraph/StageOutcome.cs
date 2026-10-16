namespace Intelligently.Core.Agents.StateGraph;

/// <summary>
/// What a stage handler returns: the next stage and a human-readable reason.
/// Handlers are the only code allowed to choose a next stage; the runner
/// validates the choice against <see cref="StageTransitions"/>.
/// </summary>
public sealed record StageOutcome(AgentStage Next, string? Reason = null)
{
    public static StageOutcome To(AgentStage next, string? reason = null) => new(next, reason);
}
