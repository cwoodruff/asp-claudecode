using System.Text.Json;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Context;

/// <summary>
/// Episodic memory over <see cref="AgentEpisode"/>: one row per finished run, recalled by learner and
/// task type with trigram similarity on the task text. The <see cref="AgentEpisode.Embedding"/> column is
/// stored but not filled; vector similarity arrives in a later phase.
/// </summary>
public sealed class EpisodicMemoryStore : IEpisodicMemoryStore
{
    private readonly IntelligentlyDbContext _db;
    private readonly AgentsOptions _options;

    public EpisodicMemoryStore(IntelligentlyDbContext db, IOptions<AgentsOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public Task<AgentEpisode> WriteAsync(AgentRun run, string outcome, string summary, IReadOnlyDictionary<string, int> toolUsage, CancellationToken ct = default)
    {
        var max = _options.Context.SummaryMaxChars;
        var episode = new AgentEpisode
        {
            RunId = run.Id,
            UserId = run.UserId,
            CourseId = run.CourseId,
            TaskType = run.TaskType,
            TaskText = TaskText.From(run),
            Outcome = outcome,
            Summary = summary.Length > max ? summary[..max] : summary,
            ToolUsageJson = JsonSerializer.Serialize(toolUsage.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value)),
        };
        _db.AgentEpisodes.Add(episode);
        return Task.FromResult(episode);
    }

    public async Task<IReadOnlyList<EpisodeMatch>> RecallAsync(string userId, string taskType, string taskText, int limit, CancellationToken ct = default)
    {
        if (limit <= 0) return [];

        // Most recent episodes of the task type across learners; similarity is scored in memory so the
        // same query works on SQL Server and SQLite.
        var candidates = await _db.AgentEpisodes
            .AsNoTracking()
            .Where(e => e.TaskType == taskType)
            .OrderByDescending(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .Take(Math.Max(limit, _options.Context.EpisodeCandidates))
            .ToListAsync(ct);

        return candidates
            .Select(e => new EpisodeMatch(e, TrigramSimilarity.Compute(taskText, e.TaskText), string.Equals(e.UserId, userId, StringComparison.Ordinal)))
            .OrderByDescending(m => m.SameLearner)
            .ThenByDescending(m => m.Similarity)
            .ThenByDescending(m => m.Episode.CreatedAt)
            .ThenBy(m => m.Episode.Id)
            .Take(limit)
            .ToList();
    }
}
