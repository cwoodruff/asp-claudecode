namespace Intelligently.Core.Agents.StateGraph;

/// <summary>Thrown by the runner when a handler returns an edge that is not in <see cref="StageTransitions"/>.</summary>
public sealed class IllegalStageTransitionException : InvalidOperationException
{
    public IllegalStageTransitionException(Guid runId, AgentStage from, AgentStage to)
        : base($"Run {runId}: transition {from} → {to} is not in the state graph transition table.")
    {
        RunId = runId;
        From = from;
        To = to;
    }

    public Guid RunId { get; }
    public AgentStage From { get; }
    public AgentStage To { get; }
}
