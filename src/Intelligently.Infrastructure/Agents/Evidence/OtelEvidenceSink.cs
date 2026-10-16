using System.Diagnostics;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Telemetry;
using Intelligently.Infrastructure.Data;

namespace Intelligently.Infrastructure.Agents.Evidence;

/// <summary>
/// Evidence Plane: one span per stage from the shared <see cref="AgentTelemetry.ActivitySource"/>
/// and append-only <see cref="AgentTrace"/> rows. Rows are committed by the runner with the stage.
/// </summary>
public sealed class OtelEvidenceSink : IEvidenceSink
{
    private readonly IntelligentlyDbContext _db;

    public OtelEvidenceSink(IntelligentlyDbContext db)
    {
        _db = db;
    }

    public Activity? StartStage(AgentRun run, AgentStage stage)
    {
        var activity = AgentTelemetry.ActivitySource.StartActivity($"agent.stage.{stage}", ActivityKind.Internal);
        activity?.SetTag("agent.run_id", run.Id.ToString());
        activity?.SetTag("agent.stage", stage.ToString());
        activity?.SetTag("agent.task_type", run.TaskType);
        activity?.SetTag("agent.plan_revision", run.PlanRevision);
        activity?.SetTag("agent.step_index", run.CurrentStepIndex);
        return activity;
    }

    public Task RecordAsync(AgentRun run, AgentTrace trace, CancellationToken ct = default)
    {
        trace.RunId = run.Id;
        trace.CreatedAt = DateTime.UtcNow;
        _db.AgentTraces.Add(trace);

        var activity = Activity.Current;
        if (activity is not null)
        {
            var tags = new ActivityTagsCollection
            {
                { "agent.trace.kind", trace.Kind },
                { "agent.trace.stage", trace.Stage.ToString() },
            };
            if (trace.Model is not null) tags.Add("gen_ai.request.model", trace.Model);
            if (trace.InputTokens > 0) tags.Add("gen_ai.usage.input_tokens", trace.InputTokens);
            if (trace.OutputTokens > 0) tags.Add("gen_ai.usage.output_tokens", trace.OutputTokens);
            if (trace.CacheReadInputTokens > 0) tags.Add("gen_ai.usage.cache_read_input_tokens", trace.CacheReadInputTokens);
            activity.AddEvent(new ActivityEvent($"agent.{trace.Kind}", tags: tags));
        }

        return Task.CompletedTask;
    }
}
