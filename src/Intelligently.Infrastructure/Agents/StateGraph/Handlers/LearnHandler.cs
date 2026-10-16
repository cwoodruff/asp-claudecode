using System.Text;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Agents.Context;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>
/// Learn: write the episodic memory row for the run through the Context Plane store, with a short
/// summary from the local compaction mode (Haiku at low effort). Knowledge-base linking arrives in Phase 5.
/// </summary>
public sealed class LearnHandler : IStageHandler
{
    private const int MaxResultCharsInTranscript = 600;

    private readonly IntelligentlyDbContext _db;
    private readonly IEpisodicMemoryStore _episodes;
    private readonly IEpisodeSummarizer _summarizer;

    public LearnHandler(IntelligentlyDbContext db, IEpisodicMemoryStore episodes, IEpisodeSummarizer summarizer)
    {
        _db = db;
        _episodes = episodes;
        _summarizer = summarizer;
    }

    public AgentStage Stage => AgentStage.Learn;

    public async Task<StageOutcome> ExecuteAsync(AgentRun run, CancellationToken ct = default)
    {
        var steps = await _db.AgentSteps
            .Where(s => s.RunId == run.Id)
            .OrderBy(s => s.StartedAt)
            .ToListAsync(ct);
        var plan = await StepQueries.CurrentPlanAsync(_db, run, ct);

        var toolUsage = steps
            .SelectMany(StepQueries.Results)
            .GroupBy(r => r.Name)
            .ToDictionary(g => g.Key, g => g.Count());

        var transcript = Transcript(run, plan, steps);
        var summary = await _summarizer.SummarizeAsync(run, transcript, ct);
        if (string.IsNullOrWhiteSpace(summary))
            summary = ExtractiveSummarizer.Summarize(run, transcript);

        await _episodes.WriteAsync(run, AgentRun.StatusCompleted, summary, toolUsage, ct);

        return StageOutcome.To(AgentStage.Completed, "Episode and traces written.");
    }

    /// <summary>A compact text transcript for the summarizer: goal, then each step with its text and results.</summary>
    internal static string Transcript(AgentRun run, AgentPlan? plan, IReadOnlyList<AgentStep> steps)
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

        sb.Append("Outcome: ").AppendLine(run.Status);
        return sb.ToString();
    }
}
