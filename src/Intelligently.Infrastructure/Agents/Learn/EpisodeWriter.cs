using System.Text;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Infrastructure.Agents.Context;
using Intelligently.Infrastructure.Agents.StateGraph.Handlers;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Infrastructure.Agents.Learn;

/// <summary>
/// The one place an <see cref="AgentEpisode"/> is written (plan section 2.2, Learn row). The Learn stage
/// calls it when a run completes; the escalation resolver calls it for an escalated run that never reached
/// Learn, so an instructor's knowledge entry always has an episode to link from. A run gets exactly one
/// episode: a second call returns the existing row.
/// </summary>
public sealed class EpisodeWriter
{
    private const int MaxResultCharsInTranscript = 600;

    private readonly IntelligentlyDbContext _db;
    private readonly IEpisodicMemoryStore _episodes;
    private readonly IEpisodeSummarizer _summarizer;

    public EpisodeWriter(IntelligentlyDbContext db, IEpisodicMemoryStore episodes, IEpisodeSummarizer summarizer)
    {
        _db = db;
        _episodes = episodes;
        _summarizer = summarizer;
    }

    /// <summary>
    /// Writes the run's episode with <paramref name="outcome"/> (Completed or Failed) and returns it, or
    /// returns the episode the run already has. Nothing is saved; the caller commits.
    /// </summary>
    public async Task<AgentEpisode> WriteAsync(AgentRun run, string outcome, CancellationToken ct = default)
    {
        var existing = _db.AgentEpisodes.Local.FirstOrDefault(e => e.RunId == run.Id)
                       ?? await _db.AgentEpisodes.FirstOrDefaultAsync(e => e.RunId == run.Id, ct);
        if (existing is not null)
            return existing;

        var steps = await _db.AgentSteps
            .Where(s => s.RunId == run.Id)
            .OrderBy(s => s.StartedAt)
            .ToListAsync(ct);
        var plan = await StepQueries.CurrentPlanAsync(_db, run, ct);

        var toolUsage = steps
            .SelectMany(StepQueries.Results)
            .GroupBy(r => r.Name)
            .ToDictionary(g => g.Key, g => g.Count());

        var transcript = Transcript(run, plan, steps, outcome);
        var summary = await _summarizer.SummarizeAsync(run, transcript, ct);
        if (string.IsNullOrWhiteSpace(summary))
            summary = ExtractiveSummarizer.Summarize(run, transcript);

        return await _episodes.WriteAsync(run, outcome, summary, toolUsage, ct);
    }

    /// <summary>A compact text transcript for the summarizer: goal, then each step with its text and results.</summary>
    internal static string Transcript(AgentRun run, AgentPlan? plan, IReadOnlyList<AgentStep> steps, string outcome)
    {
        var sb = new StringBuilder();
        sb.Append("Task: ").AppendLine(TaskText.From(run));
        if (plan is not null)
        {
            sb.Append(ExtractiveSummarizer.GoalPrefix).AppendLine(plan.Goal);
            foreach (var step in plan.Steps)
                sb.Append("Plan step ").Append(step.Index).Append(": ").Append(step.Description).Append(" [tool: ").Append(step.Tool ?? "none").AppendLine("]");
        }

        foreach (var step in steps)
        {
            sb.Append("Step ").Append(step.PlanStepIndex).Append(" attempt ").Append(step.Attempt).Append(": ").Append(step.Status);
            if (!string.IsNullOrWhiteSpace(step.VerifierMessage)) sb.Append(" - ").Append(step.VerifierMessage);
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(step.AssistantText))
                sb.Append(ExtractiveSummarizer.AssistantPrefix).AppendLine(step.AssistantText.Replace('\n', ' '));
            foreach (var result in StepQueries.Results(step))
            {
                var content = result.Content.Replace('\n', ' ');
                if (content.Length > MaxResultCharsInTranscript) content = content[..MaxResultCharsInTranscript] + "…";
                sb.Append("Tool ").Append(result.Name).Append(result.IsError ? " (error): " : ": ").AppendLine(content);
            }
        }

        if (!string.IsNullOrWhiteSpace(run.StatusReason))
            sb.Append("Last transition: ").AppendLine(run.StatusReason);
        sb.Append("Outcome: ").AppendLine(outcome);
        return sb.ToString();
    }
}
