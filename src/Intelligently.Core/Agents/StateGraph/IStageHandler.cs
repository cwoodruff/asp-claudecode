using Intelligently.Core.Agents.Entities;

namespace Intelligently.Core.Agents.StateGraph;

/// <summary>
/// Executes one stage of the state graph for a run and returns the next stage.
/// Handlers mutate the run in memory; the runner validates the transition and commits.
/// </summary>
public interface IStageHandler
{
    AgentStage Stage { get; }
    Task<StageOutcome> ExecuteAsync(AgentRun run, CancellationToken ct = default);
}
