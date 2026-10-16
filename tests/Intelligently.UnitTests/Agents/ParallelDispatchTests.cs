using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// Several tool_use blocks in one assistant turn execute concurrently and come back as one list of
/// results in tool_use order, failures included as <c>is_error</c>; the next envelope carries them as one
/// user message; a result that violates the tool's output schema reaches Verify as a failure.
/// </summary>
public class ParallelDispatchTests
{
    /// <summary>Completes once <c>count</c> participants have arrived; never blocks a thread.</summary>
    private sealed class AsyncBarrier(int count)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = count;

        public Task Arrive()
        {
            if (Interlocked.Decrement(ref _remaining) == 0) _all.TrySetResult();
            return _all.Task;
        }
    }

    /// <summary>A ReadOnly tool that only completes once every expected sibling has started, so sequential execution would stall.</summary>
    private sealed class RendezvousTool : IToolHandler
    {
        private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { n = new { type = "integer" } },
            required = new[] { "n" },
            additionalProperties = false,
        });

        private readonly AsyncBarrier _started;
        private readonly int _delayMs;

        public RendezvousTool(string name, AsyncBarrier started, int delayMs)
        {
            _started = started;
            _delayMs = delayMs;
            Descriptor = new ToolDescriptor(name, "Waits for its siblings.", Schema, ToolRiskLevel.ReadOnly);
        }

        public ToolDescriptor Descriptor { get; }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct = default)
        {
            try
            {
                await _started.Arrive().WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            catch (TimeoutException)
            {
                return ToolResult.Error(call, "siblings never started: the dispatcher ran the calls one at a time");
            }
            await Task.Delay(_delayMs, ct);
            return ToolResult.Ok(call, $"{{\"tool\":\"{Descriptor.Name}\"}}");
        }
    }

    /// <summary>A ReadOnly tool with a declared output schema whose result may or may not honour it.</summary>
    private sealed class ShapedTool : IToolHandler
    {
        public const string ToolName = "shaped";
        private readonly string _content;

        public ShapedTool(string content)
        {
            _content = content;
            Descriptor = new ToolDescriptor(
                ToolName,
                "Returns a shaped result.",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }),
                ToolRiskLevel.ReadOnly,
                OutputSchema: JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new { exit_code = new { type = "integer" }, stdout = new { type = "string" } },
                    required = new[] { "exit_code", "stdout" },
                    additionalProperties = false,
                }));
        }

        public ToolDescriptor Descriptor { get; }

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct = default) =>
            Task.FromResult(ToolResult.Ok(call, _content));
    }

    private static ToolCall Call(string tool, string id) => new(id, tool, JsonSerializer.SerializeToElement(new { n = 1 }));

    [Fact]
    public async Task Calls_of_one_turn_execute_concurrently_and_results_come_back_in_tool_use_order()
    {
        var started = new AsyncBarrier(3);
        var slow = new RendezvousTool("slow", started, 150);
        var medium = new RendezvousTool("medium", started, 50);
        var fast = new RendezvousTool("fast", started, 0);
        var failing = new StubTool("failing", ToolRiskLevel.ReadOnly, courseScopeArgument: null, execute: _ => throw new InvalidOperationException("kaboom"));
        using var host = new AgentTestHost(s => s.UseTools(slow, medium, fast, failing));
        var run = await host.CreateRunAsync();

        using var scope = host.Provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var dispatch = await dispatcher.DispatchAsync(run, [
            Call("slow", "toolu_1"),
            StubTool.Call("failing", null, id: "toolu_2"),
            Call("medium", "toolu_3"),
            Call("fast", "toolu_4"),
        ]);

        dispatch.RequiresApproval.Should().BeFalse();
        dispatch.Results.Select(r => r.ToolCallId).Should().Equal("toolu_1", "toolu_2", "toolu_3", "toolu_4");
        dispatch.Results.Select(r => r.IsError).Should().Equal(false, true, false, false);
        dispatch.Results[1].ErrorKind.Should().Be(ToolResult.ErrorKindToolFailure);
        dispatch.Results[1].Content.Should().Contain("kaboom");
        dispatch.Results[0].Content.Should().Contain("slow");
        var traces = await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id && t.Kind == AgentTrace.KindToolCall).ToListAsync());
        traces.Should().HaveCount(0, "the dispatcher only stages trace rows; the runner commits them with the stage");
    }

    [Fact]
    public async Task All_results_of_the_turn_land_in_one_user_message_in_tool_use_order()
    {
        var started = new AsyncBarrier(2);
        var a = new RendezvousTool("alpha", started, 20);
        var b = new RendezvousTool("beta", started, 0);
        var failing = new StubTool("failing", ToolRiskLevel.ReadOnly, courseScopeArgument: null, execute: _ => throw new InvalidOperationException("kaboom"));
        using var host = new AgentTestHost(s => s.UseTools(a, b, failing));

        var plan = new AgentPlan
        {
            Goal = "Call everything at once.",
            Steps =
            [
                new PlanStep { Index = 0, Description = "Fan out.", Tool = null, SuccessCriteria = "Every call answered." },
                new PlanStep { Index = 1, Description = "Reflect.", Tool = null, SuccessCriteria = "Text." },
            ],
        };
        EngineStubs.PlanReturns(host.Engine, plan);
        var envelopes = new List<PromptEnvelope>();
        var turn = 0;
        host.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<PromptEnvelope, CancellationToken>((e, _) => envelopes.Add(e))
            .ReturnsAsync(() => ++turn == 1
                ? EngineStubs.Decision(Call("alpha", "toolu_a"), StubTool.Call("failing", null, id: "toolu_f"), Call("beta", "toolu_b"))
                : EngineStubs.Decision());

        var run = await host.CreateRunAsync();
        await host.StepAsync(run.Id);   // Interpret
        await host.StepAsync(run.Id);   // Plan
        await host.StepAsync(run.Id);   // Act: three calls
        await host.StepAsync(run.Id);   // Observe
        var verified = await host.StepAsync(run.Id);   // Verify: the failing call fails the step
        verified.Stage.Should().Be(AgentStage.Recover);
        await host.StepAsync(run.Id);   // Recover: retry
        await host.StepAsync(run.Id);   // Act again: the second envelope replays the first turn

        envelopes.Should().HaveCount(2);
        var transcript = envelopes[1].Messages.Where(m => m.Section == PromptSection.Transcript).ToList();
        var assistant = transcript.Single(m => m.Role == PromptMessage.AssistantRole && m.Content.Any(c => c.Kind == PromptContent.ToolUseKind));
        var results = transcript[transcript.IndexOf(assistant) + 1];
        results.Role.Should().Be(PromptMessage.UserRole);
        var blocks = results.Content.Where(c => c.Kind == PromptContent.ToolResultKind).ToList();
        blocks.Select(b => b.ToolUseId).Should().Equal("toolu_a", "toolu_f", "toolu_b");
        blocks.Select(b => b.IsError).Should().Equal(false, true, false);
        transcript.Count(m => m.Content.Any(c => c.Kind == PromptContent.ToolResultKind)).Should().Be(1, "all results of a turn share one user message");
    }

    [Fact]
    public async Task Result_that_violates_the_output_schema_is_rejected_and_fails_verification()
    {
        var malformed = new ShapedTool("""{"exit_code":"zero","stdout":"hi"}""");
        using var host = new AgentTestHost(s => s.UseTools(malformed));
        EngineStubs.PlanReturns(host.Engine, StubTool.PlanFor(ShapedTool.ToolName));
        EngineStubs.DecideReturns(host.Engine, new ToolCall("toolu_s", ShapedTool.ToolName, JsonSerializer.SerializeToElement(new { })));

        var run = await host.CreateRunAsync();
        await host.StepAsync(run.Id);   // Interpret
        await host.StepAsync(run.Id);   // Plan
        await host.StepAsync(run.Id);   // Act
        await host.StepAsync(run.Id);   // Observe
        var verified = await host.StepAsync(run.Id);

        verified.Stage.Should().Be(AgentStage.Recover);
        verified.RecoveryReason.Should().Be("StepFailed", "an invalid output is worth a retry, not a replan");
        verified.StatusReason.Should().Contain("violates its declared schema").And.Contain("exit_code");
        var step = await host.QueryAsync(db => db.AgentSteps.SingleAsync(s => s.RunId == run.Id));
        var results = JsonSerializer.Deserialize<List<ToolResult>>(step.ToolResultsJson!)!;
        results.Should().ContainSingle().Which.ErrorKind.Should().Be(ToolResult.ErrorKindOutputInvalid);
        results[0].IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Result_that_matches_the_output_schema_passes_with_its_payload()
    {
        var shaped = new ShapedTool("""{"exit_code":0,"stdout":"hi"}""");
        using var host = new AgentTestHost(s => s.UseTools(shaped));
        var run = await host.CreateRunAsync();

        using var scope = host.Provider.CreateScope();
        var dispatch = await scope.ServiceProvider.GetRequiredService<IToolDispatcher>()
            .DispatchAsync(run, [new ToolCall("toolu_s", ShapedTool.ToolName, JsonSerializer.SerializeToElement(new { }))]);

        var result = dispatch.Results.Should().ContainSingle().Subject;
        result.IsError.Should().BeFalse();
        result.Payload.Should().NotBeNull();
        result.Payload!.Value.GetProperty("exit_code").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Native_mutating_tools_write_in_their_own_scope_and_the_notification_needs_approval()
    {
        using var host = new AgentTestHost();
        var run = await host.CreateRunAsync(userId: "learner-9");

        using var scope = host.Provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IToolDispatcher>();

        var escalate = new ToolCall("toolu_e", CreateEscalationTool.ToolName, JsonSerializer.SerializeToElement(new
        {
            course_id = 7, module_id = (int?)null, question = "Why does my build fail?", reason = "Needs the instructor's solution key.", priority = "High",
        }));
        var escalated = await dispatcher.DispatchAsync(run, [escalate]);
        escalated.RequiresApproval.Should().BeFalse("Mutating inside the run's course is allowed");
        var output = JsonSerializer.Deserialize<CreateEscalationOutput>(escalated.Results.Single().Content)!;
        output.EscalationId.Should().BePositive();
        output.Priority.Should().Be("High");
        var row = await host.QueryAsync(db => db.AIEscalations.SingleAsync(e => e.Id == output.EscalationId));
        row.AgentRunId.Should().Be(run.Id);
        row.UserId.Should().Be("learner-9");
        row.LearnerQuestion.Should().Be("Why does my build fail?");

        var replay = await dispatcher.DispatchAsync(run, [escalate]);
        replay.Results.Single().Content.Should().Be(escalated.Results.Single().Content, "the idempotency barrier replays the stored result");
        (await host.QueryAsync(db => db.AIEscalations.CountAsync(e => e.AgentRunId == run.Id))).Should().Be(1);

        var notify = new ToolCall("toolu_n", NotifyLearnerTool.ToolName, JsonSerializer.SerializeToElement(new { course_id = 7, channel = "Email", message = "An instructor will follow up." }));
        var notified = await dispatcher.DispatchAsync(run, [notify]);
        notified.RequiresApproval.Should().BeTrue("External tools always need approval");
        notified.PendingCall!.Name.Should().Be(NotifyLearnerTool.ToolName);
    }
}
