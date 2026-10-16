using System.Text.Json;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>Learn: write the episodic memory row for the run. Knowledge-base linking arrives in Phase 5.</summary>
public sealed class LearnHandler : IStageHandler
{
    private readonly IntelligentlyDbContext _db;

    public LearnHandler(IntelligentlyDbContext db)
    {
        _db = db;
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

        var lastText = steps.LastOrDefault(s => !string.IsNullOrWhiteSpace(s.AssistantText))?.AssistantText;
        var summary = lastText ?? plan?.Goal ?? $"Run {run.Id} completed.";

        _db.AgentEpisodes.Add(new AgentEpisode
        {
            RunId = run.Id,
            UserId = run.UserId,
            CourseId = run.CourseId,
            TaskType = run.TaskType,
            Outcome = AgentRun.StatusCompleted,
            Summary = summary.Length > 2000 ? summary[..2000] : summary,
            ToolUsageJson = JsonSerializer.Serialize(toolUsage),
        });

        return StageOutcome.To(AgentStage.Completed, "Episode and traces written.");
    }
}
