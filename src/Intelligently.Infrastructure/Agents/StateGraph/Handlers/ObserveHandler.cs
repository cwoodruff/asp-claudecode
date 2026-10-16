using System.Text.Json;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>Observe: capture and normalize the results of the current step so Verify sees one consistent shape.</summary>
public sealed class ObserveHandler : IStageHandler
{
    private readonly IntelligentlyDbContext _db;
    private readonly IEvidenceSink _evidence;
    private readonly AgentsOptions _options;

    public ObserveHandler(IntelligentlyDbContext db, IEvidenceSink evidence, IOptions<AgentsOptions> options)
    {
        _db = db;
        _evidence = evidence;
        _options = options.Value;
    }

    public AgentStage Stage => AgentStage.Observe;

    public async Task<StageOutcome> ExecuteAsync(AgentRun run, CancellationToken ct = default)
    {
        var step = await StepQueries.LatestAsync(_db, run, ct);
        if (step is null)
            return StageOutcome.To(AgentStage.Verify, "No step to observe.");

        var results = StepQueries.Results(step);
        var normalized = results.Select(r => r with
        {
            Content = r.Content.Length > _options.MaxToolResultChars
                ? r.Content[.._options.MaxToolResultChars]
                : r.Content,
        }).ToList();

        step.ToolResultsJson = JsonSerializer.Serialize(normalized);
        step.Status = AgentStep.StatusObserved;

        await _evidence.RecordAsync(run, new AgentTrace
        {
            Stage = Stage,
            StepId = step.Id,
            Kind = AgentTrace.KindNote,
            Payload = JsonSerializer.Serialize(new
            {
                stepIndex = step.PlanStepIndex,
                attempt = step.Attempt,
                results = normalized.Count,
                errors = normalized.Count(r => r.IsError),
            }),
        }, ct);

        return StageOutcome.To(AgentStage.Verify, $"{normalized.Count} result(s) captured.");
    }
}

/// <summary>Shared queries over <see cref="AgentStep"/> rows.</summary>
internal static class StepQueries
{
    public static Task<AgentStep?> LatestAsync(IntelligentlyDbContext db, AgentRun run, CancellationToken ct) =>
        db.AgentSteps
            .Where(s => s.RunId == run.Id)
            .OrderByDescending(s => s.StartedAt)
            .ThenByDescending(s => s.Attempt)
            .FirstOrDefaultAsync(ct);

    public static Task<AgentPlan?> CurrentPlanAsync(IntelligentlyDbContext db, AgentRun run, CancellationToken ct) =>
        db.AgentPlans
            .Where(p => p.RunId == run.Id && p.Revision == run.PlanRevision)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public static IReadOnlyList<ToolResult> Results(AgentStep step) =>
        string.IsNullOrWhiteSpace(step.ToolResultsJson)
            ? []
            : JsonSerializer.Deserialize<List<ToolResult>>(step.ToolResultsJson) ?? [];
}
