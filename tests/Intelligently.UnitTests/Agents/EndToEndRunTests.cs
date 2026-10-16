using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Entities.AI;
using Intelligently.Infrastructure.Agents.Execution;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>One run driven end to end through the real runner, handlers, dispatcher, policy and the lookup_knowledge tool.</summary>
public class EndToEndRunTests
{
    private static Task SeedKnowledgeAsync(AgentTestHost host) => host.SaveAsync(db =>
    {
        db.AIKnowledgeEntries.Add(new AIKnowledgeEntry
        {
            CourseId = 7,
            EntryType = "LabError",
            Question = "What does CS0246 mean?",
            Answer = "CS0246: the type or namespace could not be found. Check your using directives and project references.",
            Tags = "csharp,build",
            AuthoredByUserId = "instructor-1",
        });
        return Task.CompletedTask;
    });

    [Fact]
    public async Task Run_walks_the_happy_path_through_lookup_knowledge_and_completes()
    {
        using var host = new AgentTestHost();
        await SeedKnowledgeAsync(host);
        EngineStubs.PlanReturns(host.Engine, EngineStubs.LookupPlan());
        EngineStubs.DecideReturns(host.Engine, EngineStubs.LookupCall("CS0246"));

        var run = await host.CreateRunAsync();
        var (final, stages) = await host.RunToEndAsync(run.Id);

        stages.Should().Equal(
            AgentStage.Interpret, AgentStage.Plan, AgentStage.Act, AgentStage.Observe,
            AgentStage.Verify, AgentStage.Learn, AgentStage.Completed);
        final.Status.Should().Be(AgentRun.StatusCompleted);
        final.CompletedAt.Should().NotBeNull();
        final.TotalInputTokens.Should().Be(240, "two model calls of 120 input tokens");
        final.TotalOutputTokens.Should().Be(80);
        final.Budget.TokensLeft.Should().Be(300_000 - 320);
        final.Budget.ToolCallsLeft.Should().Be(39);
        final.Budget.ReplansLeft.Should().Be(3);
        final.Budget.RetriesLeft.Should().Be(2);

        var step = await host.QueryAsync(db => db.AgentSteps.SingleAsync(s => s.RunId == run.Id));
        step.Status.Should().Be(AgentStep.StatusPassed);
        step.PlanStepIndex.Should().Be(0);
        step.Attempt.Should().Be(1);
        var results = JsonSerializer.Deserialize<List<ToolResult>>(step.ToolResultsJson!)!;
        results.Should().ContainSingle();
        results[0].Name.Should().Be(LookupKnowledgeTool.ToolName);
        results[0].IsError.Should().BeFalse();
        results[0].Content.Should().Contain("could not be found").And.Contain("\"count\":1");

        var plan = await host.QueryAsync(db => db.AgentPlans.SingleAsync(p => p.RunId == run.Id));
        plan.Revision.Should().Be(0);
        plan.Steps.Should().ContainSingle(s => s.Tool == LookupKnowledgeTool.ToolName);
        plan.Hash.Should().HaveLength(64);

        var episode = await host.QueryAsync(db => db.AgentEpisodes.SingleAsync(e => e.RunId == run.Id));
        episode.Outcome.Should().Be(AgentRun.StatusCompleted);
        episode.ToolUsageJson.Should().Contain($"\"{LookupKnowledgeTool.ToolName}\":1");

        var traces = await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id).OrderBy(t => t.Id).ToListAsync());
        traces.Where(t => t.Kind == AgentTrace.KindTransition).Select(t => t.Stage).Should().Equal(
            AgentStage.Interpret, AgentStage.Plan, AgentStage.Act, AgentStage.Observe, AgentStage.Verify, AgentStage.Learn);
        traces.Count(t => t.Kind == AgentTrace.KindModelCall).Should().Be(2);
        traces.Should().ContainSingle(t => t.Kind == AgentTrace.KindToolCall).Which.Payload.Should().Contain("\"decision\":\"Allow\"");
        traces.Where(t => t.Kind == AgentTrace.KindModelCall).Should().OnlyContain(t => t.Model == "claude-opus-5-5" && t.InputTokens == 120);

        host.Engine.Verify(e => e.DecideAsync(It.Is<PromptEnvelope>(p => p.Tools.Count == 1 && p.Tools[0].Name == LookupKnowledgeTool.ToolName), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Invalid_task_spec_fails_at_Interpret_without_calling_the_model()
    {
        using var host = new AgentTestHost();
        var run = await host.CreateRunAsync(taskSpec: "not json");

        var (final, stages) = await host.RunToEndAsync(run.Id);

        stages.Should().Equal(AgentStage.Interpret, AgentStage.Failed);
        final.Status.Should().Be(AgentRun.StatusFailed);
        final.StatusReason.Should().Contain("not valid JSON");
        host.Engine.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Planner_failure_goes_through_Recover_and_replans_until_the_budget_is_spent()
    {
        using var host = new AgentTestHost();
        EngineStubs.PlanFails(host.Engine, "no plan");
        var run = await host.CreateRunAsync();

        var (final, stages) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusFailed);
        final.PlanRevision.Should().Be(3);
        final.Budget.ReplansLeft.Should().Be(0);
        stages.Should().Equal(
            AgentStage.Interpret,
            AgentStage.Plan, AgentStage.Recover,
            AgentStage.Plan, AgentStage.Recover,
            AgentStage.Plan, AgentStage.Recover,
            AgentStage.Plan, AgentStage.Recover,
            AgentStage.Failed);
        host.Engine.Verify(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
        (await host.QueryAsync(db => db.AIEscalations.CountAsync(e => e.AgentRunId == run.Id))).Should().Be(1);
    }

    [Fact]
    public async Task Step_that_skips_its_required_tool_fails_verification_and_is_retried()
    {
        using var host = new AgentTestHost();
        await SeedKnowledgeAsync(host);
        EngineStubs.PlanReturns(host.Engine, EngineStubs.LookupPlan());
        var attempt = 0;
        host.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++attempt == 1
                ? new DecisionResult([], "I think it is a typo.", "end_turn", EngineStubs.Usage, "{}")
                : new DecisionResult([EngineStubs.LookupCall()], null, "tool_use", EngineStubs.Usage, "{}"));

        var run = await host.CreateRunAsync();
        var (final, stages) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusCompleted);
        final.Budget.RetriesLeft.Should().Be(1);
        stages.Should().Equal(
            AgentStage.Interpret, AgentStage.Plan,
            AgentStage.Act, AgentStage.Observe, AgentStage.Verify, AgentStage.Recover,
            AgentStage.Act, AgentStage.Observe, AgentStage.Verify, AgentStage.Learn, AgentStage.Completed);
        var steps = await host.QueryAsync(db => db.AgentSteps.Where(s => s.RunId == run.Id).OrderBy(s => s.Attempt).ToListAsync());
        steps.Select(s => s.Status).Should().Equal(AgentStep.StatusFailed, AgentStep.StatusPassed);
        steps[0].VerifierMessage.Should().Contain("required tool");
    }

    [Fact]
    public async Task Unknown_tool_call_is_denied_and_reported_as_an_error_result()
    {
        using var host = new AgentTestHost();
        EngineStubs.PlanReturns(host.Engine, EngineStubs.LookupPlan(tool: null));
        EngineStubs.DecideReturns(host.Engine, new ToolCall("toolu_02", "delete_course", JsonSerializer.SerializeToElement(new { id = 1 })));

        var run = await host.CreateRunAsync();
        await host.StepAsync(run.Id);                 // Interpret
        await host.StepAsync(run.Id);                 // Plan
        (await host.StepAsync(run.Id)).Stage.Should().Be(AgentStage.Observe);
        await host.StepAsync(run.Id);                 // Observe
        var verified = await host.StepAsync(run.Id);  // Verify

        verified.Stage.Should().Be(AgentStage.Recover);
        verified.StatusReason.Should().Contain("Unknown tool");
    }
}
