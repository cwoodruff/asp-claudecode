using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Entities.AI;
using Intelligently.Core.Exceptions;
using Intelligently.Core.Telemetry;
using Intelligently.Infrastructure.Agents.Evidence;
using Intelligently.Infrastructure.Agents.Execution;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// Evidence Plane (plan section 1.5): span hierarchy and attributes captured with an ActivityListener,
/// token and cost metrics on the meter, append-only enforcement on AgentTrace, and the run totals event.
/// </summary>
[Collection("ActivityListener")]
public class EvidencePlaneTests
{
    /// <summary>Records every activity from the agent source until disposed.</summary>
    private sealed class SpanRecorder : IDisposable
    {
        private readonly ActivityListener _listener;
        public List<Activity> Spans { get; } = [];

        public SpanRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == AgentTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => { lock (Spans) Spans.Add(activity); },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IEnumerable<Activity> Named(string name) => Spans.Where(s => s.OperationName == name);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Sums the agent meter's counters and histograms by instrument name and tag set.</summary>
    private sealed class MeterRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        public List<(string Instrument, double Value, Dictionary<string, object?> Tags)> Measurements { get; } = [];

        public MeterRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AgentTelemetry.MeterName) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.Start();
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags) dict[tag.Key] = tag.Value;
            lock (Measurements) Measurements.Add((instrument.Name, value, dict));
        }

        public double Sum(string instrument, Func<Dictionary<string, object?>, bool>? where = null) =>
            Measurements.Where(m => m.Instrument == instrument && (where is null || where(m.Tags))).Sum(m => m.Value);

        public void Dispose() => _listener.Dispose();
    }

    private static Task SeedKnowledgeAsync(AgentTestHost host) => host.SaveAsync(db =>
    {
        db.AIKnowledgeEntries.Add(new AIKnowledgeEntry
        {
            CourseId = 7, EntryType = "LabError", Question = "What does CS0246 mean?",
            Answer = "CS0246: the type or namespace could not be found.", Tags = "csharp,build", AuthoredByUserId = "instructor-1",
        });
        return Task.CompletedTask;
    });

    private static readonly ModelCallMetadata Metadata = new("I should look up CS0246 first.", "tool_use", "medium", 123.4);

    [Fact]
    public async Task A_run_produces_one_root_span_one_span_per_stage_and_grandchildren_per_model_and_tool_call()
    {
        using var spans = new SpanRecorder();
        using var host = new AgentTestHost();
        await SeedKnowledgeAsync(host);
        host.Engine.Setup(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanResult(EngineStubs.LookupPlan(), new ModelUsage("claude-opus-5-5", 120, 40, 1000, 500), "{\"raw\":\"plan\"}", null, "[]", Metadata with { Effort = "high", StopReason = "end_turn" }));
        host.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult([EngineStubs.LookupCall()], "Looking that up.", "tool_use", EngineStubs.Usage, "{\"raw\":\"decision\"}", "[]", Metadata));

        var run = await host.CreateRunAsync();
        var (final, _) = await host.RunToEndAsync(run.Id);
        final.Status.Should().Be(AgentRun.StatusCompleted);

        // Root span: started at creation, ids persisted on the run.
        var root = spans.Named("agent.run").Should().ContainSingle().Subject;
        final.TraceId.Should().Be(root.TraceId.ToHexString());
        final.RootSpanId.Should().Be(root.SpanId.ToHexString());
        root.GetTagItem("agent.run_id").Should().Be(run.Id.ToString());
        root.GetTagItem("agent.task_type").Should().Be("LabErrorDiagnosis");

        // One child per stage, all under the root, in graph order.
        var stages = spans.Spans.Where(s => s.OperationName.StartsWith("agent.stage.", StringComparison.Ordinal)).OrderBy(s => s.StartTimeUtc).ToList();
        stages.Select(s => s.OperationName).Should().Equal(
            "agent.stage.Interpret", "agent.stage.Plan", "agent.stage.Act", "agent.stage.Observe", "agent.stage.Verify", "agent.stage.Learn");
        stages.Should().OnlyContain(s => s.TraceId == root.TraceId && s.ParentSpanId == root.SpanId);
        stages.Last().GetTagItem("agent.next_stage").Should().Be("Completed");
        stages.Last().GetTagItem("agent.status").Should().Be("Completed");
        stages.Last().GetTagItem("agent.run.total_cost_usd").Should().NotBeNull("the terminal stage carries the run totals");

        // Model calls are grandchildren of their stage with the section 1.5 attributes.
        var modelCalls = spans.Named("agent.model_call").OrderBy(s => s.StartTimeUtc).ToList();
        modelCalls.Should().HaveCount(2);
        var planCall = modelCalls[0];
        planCall.ParentSpanId.Should().Be(stages.Single(s => s.OperationName == "agent.stage.Plan").SpanId);
        planCall.GetTagItem("gen_ai.request.model").Should().Be("claude-opus-5-5");
        planCall.GetTagItem("gen_ai.request.effort").Should().Be("high");
        planCall.GetTagItem("gen_ai.usage.input_tokens").Should().Be(120);
        planCall.GetTagItem("gen_ai.usage.output_tokens").Should().Be(40);
        planCall.GetTagItem("gen_ai.usage.cache_read_input_tokens").Should().Be(1000);
        planCall.GetTagItem("gen_ai.usage.cache_creation_input_tokens").Should().Be(500);
        planCall.GetTagItem("gen_ai.response.finish_reason").Should().Be("end_turn");
        planCall.GetTagItem("agent.latency_ms").Should().Be(123.4);
        ((double)planCall.GetTagItem("agent.cost_usd")!).Should().BeApproximately(0.00048 + 0.0008 + 0.0002 + 0.0025, 1e-9,
            "120 in at $4, 40 out at $20, 1000 cache reads at $0.20, 500 cache writes at $5 per million");

        var actCall = modelCalls[1];
        actCall.ParentSpanId.Should().Be(stages.Single(s => s.OperationName == "agent.stage.Act").SpanId);
        actCall.GetTagItem("gen_ai.request.effort").Should().Be("medium");
        actCall.GetTagItem("gen_ai.response.finish_reason").Should().Be("tool_use");

        // The tool call is a grandchild of Act with tool name, risk level, PDP decision and latency.
        var toolCall = spans.Named($"agent.tool_call.{LookupKnowledgeTool.ToolName}").Should().ContainSingle().Subject;
        toolCall.ParentSpanId.Should().Be(stages.Single(s => s.OperationName == "agent.stage.Act").SpanId);
        toolCall.GetTagItem("agent.tool.name").Should().Be(LookupKnowledgeTool.ToolName);
        toolCall.GetTagItem("agent.tool.risk_level").Should().Be("ReadOnly");
        toolCall.GetTagItem("agent.pdp.decision").Should().Be("Allow");
        toolCall.GetTagItem("agent.tool.is_error").Should().Be(false);
        toolCall.GetTagItem("agent.latency_ms").Should().BeOfType<double>();
    }

    [Fact]
    public async Task Model_call_rows_carry_envelope_response_reasoning_summary_stop_reason_effort_latency_and_cost()
    {
        using var host = new AgentTestHost();
        await SeedKnowledgeAsync(host);
        host.Engine.Setup(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanResult(EngineStubs.LookupPlan(), EngineStubs.Usage, "{\"raw\":\"plan\"}", null, "[{\"type\":\"text\",\"text\":\"plan\"}]", Metadata with { Effort = "high", StopReason = "end_turn" }));
        host.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult([EngineStubs.LookupCall()], "Looking that up.", "tool_use", EngineStubs.Usage, "{\"raw\":\"decision\"}", "[]", Metadata));

        var run = await host.CreateRunAsync();
        var (final, _) = await host.RunToEndAsync(run.Id);

        var traces = await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id).OrderBy(t => t.Id).ToListAsync());
        var plan = traces.Single(t => t.Kind == AgentTrace.KindModelCall && t.Stage == AgentStage.Plan);
        plan.ReasoningSummary.Should().Be("I should look up CS0246 first.");
        plan.StopReason.Should().Be("end_turn");
        plan.Effort.Should().Be("high");
        plan.LatencyMs.Should().Be(123.4);
        plan.CostUsd.Should().Be(0.00128m, "120 × $4 + 40 × $20 per million");
        plan.StepId.Should().BeNull();
        using (var payload = JsonDocument.Parse(plan.Payload))
        {
            var root = payload.RootElement;
            root.GetProperty("envelope").GetProperty("system").EnumerateArray().Should().NotBeEmpty("the exact envelope sent is stored");
            root.GetProperty("envelope").GetProperty("messages").EnumerateArray().Should().NotBeEmpty();
            root.GetProperty("response").GetString().Should().Be("{\"raw\":\"plan\"}");
            root.GetProperty("content").EnumerateArray().Single().GetProperty("type").GetString().Should().Be("text");
            root.GetProperty("reasoningSummary").GetString().Should().Be("I should look up CS0246 first.");
        }

        var step = await host.QueryAsync(db => db.AgentSteps.SingleAsync(s => s.RunId == run.Id));
        var act = traces.Single(t => t.Kind == AgentTrace.KindModelCall && t.Stage == AgentStage.Act);
        act.StepId.Should().Be(step.Id);
        act.Effort.Should().Be("medium");

        var tool = traces.Single(t => t.Kind == AgentTrace.KindToolCall);
        tool.StepId.Should().Be(step.Id, "tool rows are attributed to their step");
        tool.LatencyMs.Should().NotBeNull();
        tool.Payload.Should().Contain("\"payload\":").And.Contain("\"riskLevel\":\"ReadOnly\"");

        final.TotalCostUsd.Should().Be(0.00256m, "two identical calls");
        traces.Sum(t => t.CostUsd).Should().Be(final.TotalCostUsd);
    }

    [Fact]
    public async Task Sandbox_runs_leave_a_sandbox_log_row_with_exit_code_and_output()
    {
        using var host = new AgentTestHost(s => s.AddSingleton<ISandbox>(new FakeSandbox()));
        EngineStubs.PlanReturns(host.Engine, LabSpecs.SandboxPlan());
        EngineStubs.DecideReturns(host.Engine, LabCode.Call());

        var run = await host.CreateRunAsync();
        var (final, _) = await host.RunToEndAsync(run.Id);
        final.Status.Should().Be(AgentRun.StatusCompleted);

        var log = await host.QueryAsync(db => db.AgentTraces.SingleAsync(t => t.RunId == run.Id && t.Kind == AgentTrace.KindSandboxLog));
        log.Stage.Should().Be(AgentStage.Act);
        log.StepId.Should().NotBeNull();
        using var payload = JsonDocument.Parse(log.Payload);
        payload.RootElement.GetProperty("sandbox").GetString().Should().Be("fake");
        payload.RootElement.GetProperty("exitCode").GetInt32().Should().Be(0);
        payload.RootElement.GetProperty("stdout").GetString().Should().Contain("ok", "the fake sandbox prints ok");
        payload.RootElement.GetProperty("call").GetProperty("Name").GetString().Should().Be(RunLabCodeTool.ToolName);
    }

    [Fact]
    public async Task Token_and_cost_counters_and_the_stage_duration_histogram_are_recorded_by_model_and_stage()
    {
        using var meter = new MeterRecorder();
        using var host = new AgentTestHost();
        await SeedKnowledgeAsync(host);
        host.Engine.Setup(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanResult(EngineStubs.LookupPlan(), new ModelUsage("claude-opus-5-5", 120, 40, 1000, 500), "{}"));
        EngineStubs.DecideReturns(host.Engine, EngineStubs.LookupCall());

        var run = await host.CreateRunAsync();
        await host.RunToEndAsync(run.Id);

        bool Plan(Dictionary<string, object?> t) => Equals(t[AgentTelemetry.TagStage], "Plan") && Equals(t[AgentTelemetry.TagModel], "claude-opus-5-5");
        meter.Sum("agent.tokens", t => Plan(t) && Equals(t[AgentTelemetry.TagTokenClass], "input")).Should().Be(120);
        meter.Sum("agent.tokens", t => Plan(t) && Equals(t[AgentTelemetry.TagTokenClass], "output")).Should().Be(40);
        meter.Sum("agent.tokens", t => Plan(t) && Equals(t[AgentTelemetry.TagTokenClass], "cache_read")).Should().Be(1000);
        meter.Sum("agent.tokens", t => Plan(t) && Equals(t[AgentTelemetry.TagTokenClass], "cache_creation")).Should().Be(500);
        meter.Sum("agent.tokens", t => Equals(t[AgentTelemetry.TagStage], "Act") && Equals(t[AgentTelemetry.TagTokenClass], "input")).Should().Be(120);
        meter.Sum("agent.cost_usd", Plan).Should().BeApproximately(0.00398, 1e-9);
        meter.Sum("agent.cost_usd", t => Equals(t[AgentTelemetry.TagStage], "Act")).Should().BeApproximately(0.00128, 1e-9);

        var durations = meter.Measurements.Where(m => m.Instrument == "agent.stage.duration").ToList();
        durations.Select(m => (string?)m.Tags[AgentTelemetry.TagStage]).Should().Contain(["Interpret", "Plan", "Act", "Observe", "Verify", "Learn"]);
        durations.Single(m => Equals(m.Tags[AgentTelemetry.TagStage], "Learn")).Tags[AgentTelemetry.TagNextStage].Should().Be("Completed");
        durations.Should().OnlyContain(m => m.Value >= 0);
    }

    [Fact]
    public async Task Finishing_a_run_writes_one_analytics_event_with_the_totals()
    {
        using var host = new AgentTestHost();
        await SeedKnowledgeAsync(host);
        EngineStubs.PlanReturns(host.Engine, EngineStubs.LookupPlan());
        EngineStubs.DecideReturns(host.Engine, EngineStubs.LookupCall());

        var run = await host.CreateRunAsync();
        var (final, _) = await host.RunToEndAsync(run.Id);

        var evt = await host.QueryAsync(db => db.AIAnalyticsEvents.SingleAsync(e => e.EventType == OtelEvidenceSink.RunFinishedEventType && e.UserId == "learner-1"));
        evt.CourseId.Should().Be(7);
        using var metadata = JsonDocument.Parse(evt.Metadata!);
        metadata.RootElement.GetProperty("runId").GetGuid().Should().Be(run.Id);
        metadata.RootElement.GetProperty("status").GetString().Should().Be(AgentRun.StatusCompleted);
        metadata.RootElement.GetProperty("inputTokens").GetInt32().Should().Be(final.TotalInputTokens);
        metadata.RootElement.GetProperty("outputTokens").GetInt32().Should().Be(final.TotalOutputTokens);
        metadata.RootElement.GetProperty("costUsd").GetDecimal().Should().Be(final.TotalCostUsd);
        metadata.RootElement.GetProperty("traceId").GetString().Should().Be(final.TraceId);
    }

    [Fact]
    public async Task Escalated_runs_also_write_the_totals_event()
    {
        using var host = new AgentTestHost();
        EngineStubs.PlanFails(host.Engine, "no plan");
        var run = await host.CreateRunAsync();

        var (final, _) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusFailed);
        var events = await host.QueryAsync(db => db.AIAnalyticsEvents.Where(e => e.EventType == OtelEvidenceSink.RunFinishedEventType).ToListAsync());
        events.Should().ContainSingle().Which.Metadata.Should().Contain("\"status\":\"Failed\"");
    }

    [Fact]
    public async Task Updating_an_agent_trace_row_is_rejected_at_SaveChanges()
    {
        using var host = new AgentTestHost();
        var run = await host.CreateRunAsync();
        await host.SaveAsync(db =>
        {
            db.AgentTraces.Add(new AgentTrace { RunId = run.Id, Stage = AgentStage.Interpret, Kind = AgentTrace.KindNote, Payload = "{\"a\":1}" });
            return Task.CompletedTask;
        });

        using (var scope = host.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IntelligentlyDbContext>();
            var trace = await db.AgentTraces.SingleAsync(t => t.RunId == run.Id);
            trace.Payload = "{\"a\":2}";

            var update = () => db.SaveChangesAsync();
            (await update.Should().ThrowAsync<AppendOnlyViolationException>()).Which.Operation.Should().Be("update");
        }

        using (var scope = host.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IntelligentlyDbContext>();
            var trace = await db.AgentTraces.SingleAsync(t => t.RunId == run.Id);
            trace.Payload.Should().Be("{\"a\":1}", "nothing was written");
            db.AgentTraces.Remove(trace);

            var delete = () => db.SaveChanges();
            delete.Should().Throw<AppendOnlyViolationException>().Which.Operation.Should().Be("delete");
        }

        // A correction is a new row.
        await host.SaveAsync(db =>
        {
            db.AgentTraces.Add(new AgentTrace { RunId = run.Id, Stage = AgentStage.Interpret, Kind = AgentTrace.KindNote, Payload = "{\"a\":2,\"corrects\":1}" });
            return Task.CompletedTask;
        });
        (await host.QueryAsync(db => db.AgentTraces.CountAsync(t => t.RunId == run.Id))).Should().Be(2);
    }

    [Fact]
    public void Sandbox_runs_are_recognised_by_payload_shape()
    {
        var call = new ToolCall("toolu_1", "run_lab_code", JsonSerializer.SerializeToElement(new { }));
        var payload = JsonSerializer.SerializeToElement(new { sandbox = "docker", exit_code = 2, stdout = "", stderr = "boom", timed_out = false, memory_exceeded = false, output_truncated = false, duration_seconds = 0.5, succeeded = false, matches_expected_output = (bool?)null, error = (string?)null });

        var sandbox = SandboxRun.TryFrom(ToolResult.Ok(call, payload));

        sandbox.Should().NotBeNull();
        sandbox!.Sandbox.Should().Be("docker");
        sandbox.ExitCode.Should().Be(2);
        sandbox.Stderr.Should().Be("boom");
        sandbox.DurationSeconds.Should().Be(0.5);
        SandboxRun.TryFrom(ToolResult.Ok(call, "plain text")).Should().BeNull();
        SandboxRun.TryFrom(ToolResult.Ok(call, JsonSerializer.SerializeToElement(new { count = 1 }))).Should().BeNull();
        TraceWriter.SandboxLog(AgentStage.Act, call, ToolResult.Ok(call, payload))!.Payload.Should().Contain("\"exitCode\":2");
    }
}

/// <summary>Tests that install a global ActivityListener or MeterListener run one at a time.</summary>
[CollectionDefinition("ActivityListener", DisableParallelization = true)]
public class ActivityListenerCollection { }
