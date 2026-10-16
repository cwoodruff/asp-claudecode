using Intelligently.Core.Agents.Entities;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>An earlier episode and how similar its task text is to the current task (0..1).</summary>
public sealed record EpisodeMatch(AgentEpisode Episode, double Similarity, bool SameLearner);

/// <summary>Context Plane episodic memory over <see cref="AgentEpisode"/> rows (plan section 1.2).</summary>
public interface IEpisodicMemoryStore
{
    /// <summary>
    /// Adds one episode for a finished run. Nothing is saved; the runner commits with the stage.
    /// </summary>
    Task<AgentEpisode> WriteAsync(AgentRun run, string outcome, string summary, IReadOnlyDictionary<string, int> toolUsage, CancellationToken ct = default);

    /// <summary>
    /// The most recent and most similar episodes of <paramref name="taskType"/> within
    /// <paramref name="courseId"/>: this learner's episodes first (by similarity to <paramref name="taskText"/>,
    /// then recency), then other learners' of the same course. Episodes of other courses are never recalled,
    /// so a run only ever sees material its course scope allows.
    /// </summary>
    Task<IReadOnlyList<EpisodeMatch>> RecallAsync(string userId, int courseId, string taskType, string taskText, int limit, CancellationToken ct = default);
}
