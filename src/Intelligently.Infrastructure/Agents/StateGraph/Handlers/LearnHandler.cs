using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Agents.Learn;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>
/// Learn: write the episodic memory row for the completed run through the <see cref="EpisodeWriter"/>
/// (summary from the local compaction mode, Haiku at low effort). The knowledge-base link is made later,
/// when an instructor marks an escalation's outcome reusable (<c>IEscalationResolver</c>).
/// </summary>
public sealed class LearnHandler : IStageHandler
{
    private readonly EpisodeWriter _episodes;

    public LearnHandler(EpisodeWriter episodes)
    {
        _episodes = episodes;
    }

    public AgentStage Stage => AgentStage.Learn;

    public async Task<StageOutcome> ExecuteAsync(AgentRun run, CancellationToken ct = default)
    {
        var episode = await _episodes.WriteAsync(run, AgentRun.StatusCompleted, ct);
        return StageOutcome.To(AgentStage.Completed, $"Episode {episode.Id} and traces written.");
    }
}
