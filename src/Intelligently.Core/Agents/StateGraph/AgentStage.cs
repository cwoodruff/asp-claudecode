namespace Intelligently.Core.Agents.StateGraph;

/// <summary>
/// Stages of the 7-stage conditional state graph plus the three terminal/parked
/// states a run can sit in. Persisted on <see cref="Entities.AgentRun.Stage"/>.
/// </summary>
public enum AgentStage
{
    Interpret,
    Plan,
    Act,
    Observe,
    Verify,
    Recover,
    Learn,
    AwaitingApproval,
    Completed,
    Failed,
}
