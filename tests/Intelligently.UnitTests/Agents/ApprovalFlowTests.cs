using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.StateGraph.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// RequireApproval parks the run, notifies instructors, and a recorded decision resumes it:
/// approved → the same Act step runs its parked calls; denied → Recover → Plan with a new revision.
/// </summary>
public class ApprovalFlowTests
{
    private const string Tool = "delete_notes";

    private static (AgentTestHost Host, StubTool Tool, RecordingRunQueue Queue) Park()
    {
        var tool = new StubTool(Tool, ToolRiskLevel.Destructive);
        RecordingRunQueue queue = null!;
        var host = new AgentTestHost(s =>
        {
            s.UseTools(tool);
            queue = s.UseRecordingQueue();
        });
        EngineStubs.PlanReturns(host.Engine, StubTool.PlanFor(Tool));
        EngineStubs.DecideReturns(host.Engine, StubTool.Call(Tool, 7, id: "toolu_del"));
        return (host, tool, queue);
    }

    private static async Task<AgentApproval> DecideAsync(AgentTestHost host, Guid runId, bool approved, string? note = null)
    {
        using var scope = host.Provider.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<IApprovalGate>();
        var approval = await scope.ServiceProvider.GetRequiredService<Intelligently.Infrastructure.Data.IntelligentlyDbContext>()
            .AgentApprovals.AsNoTracking().SingleAsync(a => a.RunId == runId && a.Status == AgentApproval.StatusPending);
        return await gate.DecideAsync(approval.Id, approved, "instructor-1", note);
    }

    [Fact]
    public async Task Destructive_call_parks_the_run_writes_the_approval_and_escalates()
    {
        var (host, tool, queue) = Park();
        using var _ = host;

        var run = await host.CreateRunAsync();
        var (parked, stages) = await host.RunToEndAsync(run.Id);

        stages.Should().Equal(AgentStage.Interpret, AgentStage.Plan, AgentStage.Act, AgentStage.AwaitingApproval);
        parked.Status.Should().Be(AgentRun.StatusWaiting);
        parked.IsWaitingForApproval.Should().BeTrue();
        parked.StatusReason.Should().Contain("requires approval");
        parked.Budget.ToolCallsLeft.Should().Be(39, "the call was budgeted when proposed");
        tool.Executions.Should().Be(0);

        var approval = await host.QueryAsync(db => db.AgentApprovals.SingleAsync(a => a.RunId == run.Id));
        approval.Status.Should().Be(AgentApproval.StatusPending);
        approval.ToolName.Should().Be(Tool);
        approval.ArgsHash.Should().HaveLength(64);
        approval.Reason.Should().Contain("destructive-approval");
        JsonSerializer.Deserialize<ToolCall>(approval.ToolCallJson)!.Id.Should().Be("toolu_del");
        approval.EscalationId.Should().NotBeNull();

        var step = await host.QueryAsync(db => db.AgentSteps.SingleAsync(s => s.RunId == run.Id));
        step.Status.Should().Be(AgentStep.StatusAwaitingApproval);
        approval.StepId.Should().Be(step.Id);

        var escalation = await host.QueryAsync(db => db.AIEscalations.SingleAsync(e => e.Id == approval.EscalationId));
        escalation.AgentRunId.Should().Be(run.Id);
        escalation.Status.Should().Be("New");
        escalation.EscalationReason.Should().Contain("needs approval for tool 'delete_notes'");
        escalation.AIAttemptedResponse.Should().Contain("delete_notes");

        var traces = await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id).ToListAsync());
        traces.Should().ContainSingle(t => t.Kind == AgentTrace.KindApproval).Which.Payload.Should().Contain("\"status\":\"Pending\"");
        queue.Enqueued.Should().Equal(run.Id);
    }

    [Fact]
    public async Task Approval_re_enqueues_the_run_and_resumes_the_same_Act_step_without_a_new_model_turn()
    {
        var (host, tool, queue) = Park();
        using var _ = host;
        var run = await host.CreateRunAsync();
        await host.RunToEndAsync(run.Id);
        var parkedStepId = (await host.QueryAsync(db => db.AgentSteps.SingleAsync(s => s.RunId == run.Id))).Id;

        var approval = await DecideAsync(host, run.Id, approved: true, note: "Looks fine.");

        approval.Status.Should().Be(AgentApproval.StatusApproved);
        approval.DecidedBy.Should().Be("instructor-1");
        approval.DecidedAt.Should().NotBeNull();
        queue.Enqueued.Should().Equal(run.Id, run.Id);

        var resumed = await host.LoadAsync(run.Id);
        resumed.Stage.Should().Be(AgentStage.AwaitingApproval);
        resumed.Status.Should().Be(AgentRun.StatusRunning, "the gate hands the run back to a worker");

        var (final, stages) = await host.RunToEndAsync(run.Id);

        stages.Should().Equal(AgentStage.AwaitingApproval, AgentStage.Act, AgentStage.Observe, AgentStage.Verify, AgentStage.Learn, AgentStage.Completed);
        final.Status.Should().Be(AgentRun.StatusCompleted);
        final.Budget.ToolCallsLeft.Should().Be(39, "the resumed call is not budgeted twice");
        tool.Executions.Should().Be(1);
        tool.Calls.Single().Id.Should().Be("toolu_del");
        host.Engine.Verify(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()), Times.Once,
            "the parked calls are reused; the model is not asked again");

        var steps = await host.QueryAsync(db => db.AgentSteps.Where(s => s.RunId == run.Id).ToListAsync());
        steps.Should().ContainSingle().Which.Id.Should().Be(parkedStepId, "approval resumes the same step");
        steps[0].Status.Should().Be(AgentStep.StatusPassed);

        var escalation = await host.QueryAsync(db => db.AIEscalations.SingleAsync(e => e.AgentRunId == run.Id));
        escalation.Status.Should().Be("Resolved");
        escalation.InstructorUserId.Should().Be("instructor-1");
        escalation.InstructorResponse.Should().Be("Looks fine.");

        var traces = await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id).OrderBy(t => t.Id).ToListAsync());
        traces.Where(t => t.Kind == AgentTrace.KindApproval).Should().HaveCount(2);
        traces.Last(t => t.Kind == AgentTrace.KindApproval).Payload.Should().Contain("\"status\":\"Approved\"").And.Contain("instructor-1");
        traces.Should().Contain(t => t.Kind == AgentTrace.KindTransition && t.Stage == AgentStage.AwaitingApproval);
        traces.Single(t => t.Kind == AgentTrace.KindToolCall).Payload.Should().Contain("Approved by instructor-1");
    }

    [Fact]
    public async Task Denial_goes_to_Recover_then_Plan_with_the_revision_incremented()
    {
        var (host, tool, queue) = Park();
        using var _ = host;
        var run = await host.CreateRunAsync();
        await host.RunToEndAsync(run.Id);

        var approval = await DecideAsync(host, run.Id, approved: false, note: "Not in this course.");
        approval.Status.Should().Be(AgentApproval.StatusDenied);
        queue.Enqueued.Should().Equal(run.Id, run.Id);

        var recovering = await host.StepAsync(run.Id);                // AwaitingApproval → Recover
        recovering.Stage.Should().Be(AgentStage.Recover);
        recovering.RecoveryReason.Should().Be(RecoveryReasons.PolicyDenied);
        recovering.StatusReason.Should().Contain("denied by instructor-1").And.Contain("Replan without tool 'delete_notes'");

        var replanning = await host.StepAsync(run.Id);                // Recover → Plan
        replanning.Stage.Should().Be(AgentStage.Plan);
        replanning.PlanRevision.Should().Be(1);
        replanning.CurrentStepIndex.Should().Be(0);
        replanning.Budget.ReplansLeft.Should().Be(2, "a denial spends a replan");
        replanning.Budget.RetriesLeft.Should().Be(2, "a denial is final for that call, so it is never retried");

        var step = await host.QueryAsync(db => db.AgentSteps.SingleAsync(s => s.RunId == run.Id));
        step.Status.Should().Be(AgentStep.StatusFailed);
        step.VerifierMessage.Should().Contain("Approval denied by instructor-1").And.Contain("Not in this course.");
        tool.Executions.Should().Be(0);

        var escalation = await host.QueryAsync(db => db.AIEscalations.SingleAsync(e => e.AgentRunId == run.Id));
        escalation.Status.Should().Be("Resolved");
    }

    [Fact]
    public async Task A_decision_cannot_be_recorded_twice()
    {
        var (host, _, _) = Park();
        using var __ = host;
        var run = await host.CreateRunAsync();
        await host.RunToEndAsync(run.Id);
        var approval = await DecideAsync(host, run.Id, approved: true);

        using var scope = host.Provider.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<IApprovalGate>();
        var act = () => gate.DecideAsync(approval.Id, false, "instructor-2");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already Approved*");
    }

    [Fact]
    public async Task Policy_denial_of_a_tool_call_leads_to_Recover_then_Plan_with_the_revision_incremented()
    {
        // A configured Deny rule: the dispatcher reports the call as a policy denial, Verify fails the step,
        // and Recover replans (no retry) because the denial is final for that call.
        var tool = new StubTool("post_webhook", ToolRiskLevel.External, courseScopeArgument: null);
        using var host = new AgentTestHost(s =>
        {
            s.UseTools(tool);
            s.PostConfigure<Intelligently.Core.Agents.AgentsOptions>(o =>
                o.Policy.Rules = [new PolicyRule { Name = "no-webhooks", Kind = PolicyRuleKind.Risk, Tool = "post_*", Effect = PolicyEffect.Deny }, .. PolicyRule.Defaults]);
        });
        EngineStubs.PlanReturns(host.Engine, StubTool.PlanFor("post_webhook"));
        EngineStubs.DecideReturns(host.Engine, StubTool.Call("post_webhook", null));

        var run = await host.CreateRunAsync();
        await host.StepAsync(run.Id);                                   // Interpret
        await host.StepAsync(run.Id);                                   // Plan
        (await host.StepAsync(run.Id)).Stage.Should().Be(AgentStage.Observe);
        await host.StepAsync(run.Id);                                   // Observe
        var verified = await host.StepAsync(run.Id);                    // Verify → Recover
        verified.Stage.Should().Be(AgentStage.Recover);
        verified.RecoveryReason.Should().Be(RecoveryReasons.PolicyDenied);
        verified.StatusReason.Should().Contain("Denied by policy").And.Contain("no-webhooks");

        var recovered = await host.StepAsync(run.Id);                   // Recover → Plan
        recovered.Stage.Should().Be(AgentStage.Plan);
        recovered.PlanRevision.Should().Be(1);
        recovered.Budget.ReplansLeft.Should().Be(2);
        recovered.Budget.RetriesLeft.Should().Be(2);
        tool.Executions.Should().Be(0);
    }
}
