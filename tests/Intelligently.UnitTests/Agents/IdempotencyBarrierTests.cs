using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Execution;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>A Mutating call that already executed in the run is replayed from the stored result, not executed again.</summary>
public class IdempotencyBarrierTests
{
    private const string Tool = "update_notes";

    [Fact]
    public void Canonical_hash_ignores_property_order_and_whitespace()
    {
        var a = JsonDocument.Parse("""{ "b": [1, 2, {"y": true, "x": null}], "a": "s" }""").RootElement;
        var b = JsonDocument.Parse("""{"a":"s","b":[1,2,{"x":null,"y":true}]}""").RootElement;
        var c = JsonDocument.Parse("""{"a":"s","b":[2,1,{"x":null,"y":true}]}""").RootElement;

        CanonicalJson.Serialize(a).Should().Be("""{"a":"s","b":[1,2,{"x":null,"y":true}]}""");
        CanonicalJson.Sha256(a).Should().Be(CanonicalJson.Sha256(b)).And.HaveLength(64);
        CanonicalJson.Sha256(a).Should().NotBe(CanonicalJson.Sha256(c), "array order is significant");
    }

    [Fact]
    public async Task Replayed_mutating_call_returns_the_stored_result_without_calling_the_handler()
    {
        var tool = new StubTool(Tool, ToolRiskLevel.Mutating);
        using var host = new AgentTestHost(s => s.UseTools(tool));
        // Two plan steps; the model proposes the identical call (different call id, different key order) for both.
        EngineStubs.PlanReturns(host.Engine, StubTool.PlanFor(Tool, steps: 2));
        var turn = 0;
        host.Engine.Setup(e => e.DecideAsync(It.IsAny<Intelligently.Core.Agents.Contracts.PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++turn == 1
                ? EngineStubs.Decision(new ToolCall("toolu_a", Tool, JsonSerializer.SerializeToElement(new { course_id = 7, text = "same" })))
                : EngineStubs.Decision(new ToolCall("toolu_b", Tool, JsonDocument.Parse("""{"text":"same","course_id":7}""").RootElement)));

        var run = await host.CreateRunAsync();
        var (final, stages) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusCompleted);
        stages.Should().Equal(
            AgentStage.Interpret, AgentStage.Plan,
            AgentStage.Act, AgentStage.Observe, AgentStage.Verify,
            AgentStage.Act, AgentStage.Observe, AgentStage.Verify,
            AgentStage.Learn, AgentStage.Completed);
        tool.Executions.Should().Be(1, "the second call is a replay");

        var steps = await host.QueryAsync(db => db.AgentSteps.Where(s => s.RunId == run.Id).OrderBy(s => s.PlanStepIndex).ToListAsync());
        var first = JsonSerializer.Deserialize<List<ToolResult>>(steps[0].ToolResultsJson!)!.Single();
        var second = JsonSerializer.Deserialize<List<ToolResult>>(steps[1].ToolResultsJson!)!.Single();
        second.Content.Should().Be(first.Content);
        second.ToolCallId.Should().Be("toolu_b", "the replay is addressed to the new call");
        second.IsError.Should().BeFalse();

        var records = await host.QueryAsync(db => db.IdempotencyRecords.Where(r => r.RunId == run.Id).ToListAsync());
        records.Should().ContainSingle().Which.ToolName.Should().Be(Tool);
        records[0].ArgsHash.Should().HaveLength(64);

        var toolTraces = await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id && t.Kind == AgentTrace.KindToolCall).OrderBy(t => t.Id).ToListAsync());
        toolTraces.Should().HaveCount(2);
        toolTraces[0].Payload.Should().Contain("\"replayed\":false");
        toolTraces[1].Payload.Should().Contain("\"replayed\":true").And.Contain("\"ruleKind\":\"Idempotency\"");
    }

    [Fact]
    public async Task Different_arguments_are_not_a_replay()
    {
        var tool = new StubTool(Tool, ToolRiskLevel.Mutating);
        using var host = new AgentTestHost(s => s.UseTools(tool));
        EngineStubs.PlanReturns(host.Engine, StubTool.PlanFor(Tool, steps: 2));
        var turn = 0;
        host.Engine.Setup(e => e.DecideAsync(It.IsAny<Intelligently.Core.Agents.Contracts.PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => EngineStubs.Decision(StubTool.Call(Tool, 7, text: $"text-{++turn}", id: $"toolu_{turn}")));

        var run = await host.CreateRunAsync();
        var (final, _) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusCompleted);
        tool.Executions.Should().Be(2);
        (await host.QueryAsync(db => db.IdempotencyRecords.CountAsync(r => r.RunId == run.Id))).Should().Be(2);
    }

    [Fact]
    public async Task Failed_results_are_not_stored_so_a_retry_executes_again()
    {
        var attempts = 0;
        var tool = new StubTool(Tool, ToolRiskLevel.Mutating, execute: call => ++attempts == 1
            ? ToolResult.Error(call, "transient failure")
            : ToolResult.Ok(call, "{\"ok\":true}"));
        using var host = new AgentTestHost(s => s.UseTools(tool));
        EngineStubs.PlanReturns(host.Engine, StubTool.PlanFor(Tool));
        EngineStubs.DecideReturns(host.Engine, StubTool.Call(Tool, 7));

        var run = await host.CreateRunAsync();
        var (final, stages) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusCompleted);
        final.Budget.RetriesLeft.Should().Be(1);
        stages.Should().Contain(AgentStage.Recover);
        tool.Executions.Should().Be(2, "the failed first attempt was not recorded as done");
        (await host.QueryAsync(db => db.IdempotencyRecords.CountAsync(r => r.RunId == run.Id))).Should().Be(1);
    }

    [Fact]
    public async Task ReadOnly_calls_are_executed_every_time()
    {
        var tool = new StubTool("lookup_thing", ToolRiskLevel.ReadOnly);
        using var host = new AgentTestHost(s => s.UseTools(tool));
        EngineStubs.PlanReturns(host.Engine, StubTool.PlanFor("lookup_thing", steps: 2));
        EngineStubs.DecideReturns(host.Engine, StubTool.Call("lookup_thing", 7));

        var run = await host.CreateRunAsync();
        var (final, _) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusCompleted);
        tool.Executions.Should().Be(2);
        (await host.QueryAsync(db => db.IdempotencyRecords.CountAsync(r => r.RunId == run.Id))).Should().Be(0);
        _ = LookupKnowledgeTool.ToolName;
    }
}
