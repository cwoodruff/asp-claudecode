using System.Diagnostics;
using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Entities.AI;
using Intelligently.Core.Telemetry;
using Intelligently.Infrastructure.Data;

namespace Intelligently.Infrastructure.Agents.Evidence;

/// <summary>
/// Evidence Plane (plan section 1.5) on the shared <see cref="AgentTelemetry.ActivitySource"/>: one root
/// span per run (its ids persisted on the run, so stages executed later or elsewhere attach to it), one
/// child span per stage, one grandchild per model call and per tool call, with the attributes of section
/// 1.5 (model, effort, tokens per class, cost, stop reason, tool name, risk level, PDP decision, sandbox
/// exit code, latency). Trace rows are append-only and committed by the runner with the stage; token and
/// cost counters are recorded as each row lands.
/// </summary>
public sealed class OtelEvidenceSink : IEvidenceSink
{
    public const string RunFinishedEventType = "AgentRunFinished";

    private readonly IntelligentlyDbContext _db;

    public OtelEvidenceSink(IntelligentlyDbContext db)
    {
        _db = db;
    }

    public Activity? StartRun(AgentRun run)
    {
        // The root span is a real activity so exporters see it; it is stopped at once because the run
        // outlives any process. Its ids are persisted and every stage span is parented on them.
        var activity = AgentTelemetry.ActivitySource.StartActivity("agent.run", ActivityKind.Internal);
        if (activity is null)
        {
            // No listener: still give the run a trace so later spans (if a listener appears) share it.
            run.TraceId ??= ActivityTraceId.CreateRandom().ToHexString();
            run.RootSpanId ??= ActivitySpanId.CreateRandom().ToHexString();
            return null;
        }

        run.TraceId = activity.TraceId.ToHexString();
        run.RootSpanId = activity.SpanId.ToHexString();
        TagRun(activity, run);
        activity.Stop();
        return activity;
    }

    public Activity? StartStage(AgentRun run, AgentStage stage)
    {
        if (run.TraceId is null || run.RootSpanId is null)
            StartRun(run);

        var parent = new ActivityContext(
            ActivityTraceId.CreateFromString(run.TraceId.AsSpan()),
            ActivitySpanId.CreateFromString(run.RootSpanId.AsSpan()),
            ActivityTraceFlags.Recorded);

        var activity = AgentTelemetry.ActivitySource.StartActivity($"agent.stage.{stage}", ActivityKind.Internal, parent);
        if (activity is null) return null;

        TagRun(activity, run);
        activity.SetTag("agent.stage", stage.ToString());
        activity.SetTag("agent.plan_revision", run.PlanRevision);
        activity.SetTag("agent.step_index", run.CurrentStepIndex);
        return activity;
    }

    public Activity? StartModelCall(AgentRun run, AgentStage stage, string model, string? effort)
    {
        var activity = AgentTelemetry.ActivitySource.StartActivity("agent.model_call", ActivityKind.Client);
        if (activity is null) return null;

        activity.SetTag("agent.run_id", run.Id.ToString());
        activity.SetTag("agent.stage", stage.ToString());
        activity.SetTag("gen_ai.system", "anthropic");
        activity.SetTag("gen_ai.request.model", model);
        if (effort is not null) activity.SetTag("gen_ai.request.effort", effort);
        return activity;
    }

    public Activity? StartToolCall(AgentRun run, ToolCall call, ToolDescriptor? descriptor)
    {
        var activity = AgentTelemetry.ActivitySource.StartActivity($"agent.tool_call.{call.Name}", ActivityKind.Internal);
        if (activity is null) return null;

        activity.SetTag("agent.run_id", run.Id.ToString());
        activity.SetTag("agent.stage", run.Stage.ToString());
        activity.SetTag("agent.tool.name", call.Name);
        activity.SetTag("agent.tool.call_id", call.Id);
        if (descriptor is not null) activity.SetTag("agent.tool.risk_level", descriptor.RiskLevel.ToString());
        return activity;
    }

    public Task RecordAsync(AgentRun run, AgentTrace trace, CancellationToken ct = default)
    {
        trace.RunId = run.Id;
        trace.CreatedAt = DateTime.UtcNow;
        _db.AgentTraces.Add(trace);

        if (trace.Kind == AgentTrace.KindModelCall)
            RecordUsageMetrics(trace);

        var activity = Activity.Current;
        if (activity is not null)
        {
            Annotate(activity, trace);
            activity.AddEvent(new ActivityEvent($"agent.{trace.Kind}", tags: EventTags(trace)));
        }

        return Task.CompletedTask;
    }

    public Task FinishRunAsync(AgentRun run, CancellationToken ct = default)
    {
        _db.AIAnalyticsEvents.Add(new AIAnalyticsEvent
        {
            EventType = RunFinishedEventType,
            UserId = run.UserId,
            CourseId = run.CourseId,
            Metadata = JsonSerializer.Serialize(new
            {
                runId = run.Id,
                taskType = run.TaskType,
                status = run.Status,
                stage = run.Stage.ToString(),
                statusReason = run.StatusReason,
                planRevision = run.PlanRevision,
                inputTokens = run.TotalInputTokens,
                outputTokens = run.TotalOutputTokens,
                costUsd = run.TotalCostUsd,
                traceId = run.TraceId,
                createdAt = run.CreatedAt,
                completedAt = run.CompletedAt,
            }),
        });

        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("agent.run.status", run.Status);
            activity.SetTag("agent.run.total_input_tokens", run.TotalInputTokens);
            activity.SetTag("agent.run.total_output_tokens", run.TotalOutputTokens);
            activity.SetTag("agent.run.total_cost_usd", (double)run.TotalCostUsd);
        }

        return Task.CompletedTask;
    }

    private static void TagRun(Activity activity, AgentRun run)
    {
        activity.SetTag("agent.run_id", run.Id.ToString());
        activity.SetTag("agent.task_type", run.TaskType);
        activity.SetTag("agent.course_id", run.CourseId);
        activity.SetTag("agent.user_id", run.UserId);
    }

    /// <summary>Copies the row's figures onto the span it was recorded under (the model-call or tool-call grandchild when one is current).</summary>
    private static void Annotate(Activity activity, AgentTrace trace)
    {
        switch (trace.Kind)
        {
            case AgentTrace.KindModelCall:
                if (trace.Model is not null) activity.SetTag("gen_ai.request.model", trace.Model);
                if (trace.Effort is not null) activity.SetTag("gen_ai.request.effort", trace.Effort);
                if (trace.StopReason is not null) activity.SetTag("gen_ai.response.finish_reason", trace.StopReason);
                activity.SetTag("gen_ai.usage.input_tokens", trace.InputTokens);
                activity.SetTag("gen_ai.usage.output_tokens", trace.OutputTokens);
                activity.SetTag("gen_ai.usage.cache_read_input_tokens", trace.CacheReadInputTokens);
                activity.SetTag("gen_ai.usage.cache_creation_input_tokens", trace.CacheCreationInputTokens);
                activity.SetTag("agent.cost_usd", (double)trace.CostUsd);
                if (trace.LatencyMs is { } latency) activity.SetTag("agent.latency_ms", latency);
                break;

            case AgentTrace.KindToolCall:
                if (trace.LatencyMs is { } toolLatency) activity.SetTag("agent.latency_ms", toolLatency);
                using (var doc = JsonDocument.Parse(trace.Payload))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("decision", out var decision)) activity.SetTag("agent.pdp.decision", decision.GetString());
                    if (root.TryGetProperty("riskLevel", out var risk) && risk.ValueKind == JsonValueKind.String) activity.SetTag("agent.tool.risk_level", risk.GetString());
                    if (root.TryGetProperty("result", out var result) && result.TryGetProperty("IsError", out var isError))
                        activity.SetTag("agent.tool.is_error", isError.ValueKind == JsonValueKind.True);
                }
                break;

            case AgentTrace.KindSandboxLog:
                using (var doc = JsonDocument.Parse(trace.Payload))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("sandbox", out var sandbox)) activity.SetTag("agent.sandbox.name", sandbox.GetString());
                    if (root.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number) activity.SetTag("agent.sandbox.exit_code", exit.GetInt32());
                    if (root.TryGetProperty("timedOut", out var timedOut)) activity.SetTag("agent.sandbox.timed_out", timedOut.ValueKind == JsonValueKind.True);
                }
                break;
        }
    }

    private static ActivityTagsCollection EventTags(AgentTrace trace)
    {
        var tags = new ActivityTagsCollection
        {
            { "agent.trace.kind", trace.Kind },
            { "agent.trace.stage", trace.Stage.ToString() },
        };
        if (trace.StepId is { } stepId) tags.Add("agent.step_id", stepId.ToString());
        if (trace.Model is not null) tags.Add("gen_ai.request.model", trace.Model);
        if (trace.InputTokens > 0) tags.Add("gen_ai.usage.input_tokens", trace.InputTokens);
        if (trace.OutputTokens > 0) tags.Add("gen_ai.usage.output_tokens", trace.OutputTokens);
        if (trace.CacheReadInputTokens > 0) tags.Add("gen_ai.usage.cache_read_input_tokens", trace.CacheReadInputTokens);
        if (trace.CacheCreationInputTokens > 0) tags.Add("gen_ai.usage.cache_creation_input_tokens", trace.CacheCreationInputTokens);
        if (trace.CostUsd > 0) tags.Add("agent.cost_usd", (double)trace.CostUsd);
        return tags;
    }

    private static void RecordUsageMetrics(AgentTrace trace)
    {
        var model = trace.Model ?? "unknown";
        var stage = trace.Stage.ToString();
        Count("input", trace.InputTokens);
        Count("output", trace.OutputTokens);
        Count("cache_read", trace.CacheReadInputTokens);
        Count("cache_creation", trace.CacheCreationInputTokens);
        AgentTelemetry.CostUsd.Add((double)trace.CostUsd, new TagList
        {
            { AgentTelemetry.TagModel, model },
            { AgentTelemetry.TagStage, stage },
        });

        void Count(string tokenClass, int tokens)
        {
            if (tokens <= 0) return;
            AgentTelemetry.Tokens.Add(tokens, new TagList
            {
                { AgentTelemetry.TagModel, model },
                { AgentTelemetry.TagStage, stage },
                { AgentTelemetry.TagTokenClass, tokenClass },
            });
        }
    }
}
