using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Entities.AI;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Intelligently.Infrastructure.Agents.Learn;

/// <summary>
/// Resolves an escalation raised by an agent run and feeds a reusable outcome back into the knowledge
/// base (plan section 3, Phase 5): the instructor's response becomes an <see cref="AIKnowledgeEntry"/>
/// (type from the task, source = the escalation) and the run's <see cref="AgentEpisode"/> is linked to it,
/// so later runs of the same course retrieve the answer and recall the episode together. A knowledge entry
/// is published at most once per escalation; resolving again without the flag leaves it in place.
/// </summary>
public sealed class EscalationResolver : IEscalationResolver
{
    public const string ResolvedStatus = "Resolved";
    private const int MaxQuestionChars = 2000;

    private readonly IntelligentlyDbContext _db;
    private readonly EpisodeWriter _episodes;
    private readonly IEvidenceSink _evidence;
    private readonly ILogger<EscalationResolver> _logger;

    public EscalationResolver(IntelligentlyDbContext db, EpisodeWriter episodes, IEvidenceSink evidence, ILogger<EscalationResolver> logger)
    {
        _db = db;
        _episodes = episodes;
        _evidence = evidence;
        _logger = logger;
    }

    public async Task<EscalationResolution> ResolveAsync(int escalationId, string instructorUserId, string response, bool addToKnowledgeBase, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instructorUserId))
            throw new ArgumentException("The resolution must name the instructor.", nameof(instructorUserId));
        if (string.IsNullOrWhiteSpace(response))
            throw new ArgumentException("The resolution needs a response.", nameof(response));

        var escalation = await _db.AIEscalations.FirstOrDefaultAsync(e => e.Id == escalationId, ct)
                         ?? throw new KeyNotFoundException($"Escalation {escalationId} was not found.");

        escalation.Status = ResolvedStatus;
        escalation.InstructorUserId = instructorUserId;
        escalation.InstructorResponse = response.Trim();
        escalation.ResolvedAt = DateTime.UtcNow;
        escalation.AddToKnowledgeBase = addToKnowledgeBase || escalation.AddToKnowledgeBase;

        AIKnowledgeEntry? entry = null;
        AgentEpisode? episode = null;

        if (addToKnowledgeBase)
        {
            var run = escalation.AgentRunId is { } runId
                ? await _db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId, ct)
                : null;

            entry = await _db.AIKnowledgeEntries.FirstOrDefaultAsync(k => k.SourceEscalationId == escalation.Id, ct)
                    ?? NewEntry(escalation, run, instructorUserId);

            if (run is not null)
            {
                // An escalated run ended at Failed without reaching Learn; the Learn-stage writer gives it its
                // episode now (Outcome = Failed) so the knowledge link has an episode to hang off.
                episode = await _episodes.WriteAsync(run, run.Status == AgentRun.StatusCompleted ? AgentRun.StatusCompleted : AgentRun.StatusFailed, ct);
                await _db.SaveChangesAsync(ct); // assigns the entry id for the link below
                episode.KnowledgeEntryId = entry.Id;

                await _evidence.RecordAsync(run, new AgentTrace
                {
                    Stage = AgentStage.Learn,
                    Kind = AgentTrace.KindNote,
                    Payload = JsonSerializer.Serialize(new
                    {
                        escalationId = escalation.Id,
                        knowledgeEntryId = entry.Id,
                        episodeId = episode.Id,
                        resolvedBy = instructorUserId,
                        addedToKnowledgeBase = true,
                    }),
                }, ct);
            }
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Escalation {EscalationId} resolved by {Instructor}; knowledge entry {EntryId}, episode {EpisodeId}",
            escalation.Id, instructorUserId, entry?.Id, episode?.Id);
        return new EscalationResolution(escalation, entry, episode);
    }

    private AIKnowledgeEntry NewEntry(AIEscalation escalation, AgentRun? run, string instructorUserId)
    {
        var question = escalation.LearnerQuestion.Length > MaxQuestionChars ? escalation.LearnerQuestion[..MaxQuestionChars] : escalation.LearnerQuestion;
        var entry = new AIKnowledgeEntry
        {
            CourseId = escalation.CourseId,
            ModuleId = escalation.ModuleId,
            EntryType = EntryTypeFor(run?.TaskType),
            Question = question,
            Answer = escalation.InstructorResponse ?? string.Empty,
            Tags = run is null ? "escalation" : $"escalation,{run.TaskType}",
            IsActive = true,
            AuthoredByUserId = instructorUserId,
            SourceEscalationId = escalation.Id,
        };
        _db.AIKnowledgeEntries.Add(entry);
        return entry;
    }

    /// <summary>FAQ | Misconception | LabError | Clarification | BestPractice, from the task type.</summary>
    public static string EntryTypeFor(string? taskType) => taskType switch
    {
        null => "FAQ",
        var t when t.Contains("Lab", StringComparison.OrdinalIgnoreCase) || t.Contains("Error", StringComparison.OrdinalIgnoreCase) => "LabError",
        var t when t.Contains("Misconception", StringComparison.OrdinalIgnoreCase) => "Misconception",
        var t when t.Contains("Practice", StringComparison.OrdinalIgnoreCase) => "BestPractice",
        _ => "Clarification",
    };
}
