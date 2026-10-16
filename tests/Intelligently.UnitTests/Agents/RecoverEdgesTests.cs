using FluentAssertions;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Agents.StateGraph.Handlers;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// Phase 2 Recover edges: every edge spends exactly its own counter, and oscillation
/// (a repeated plan hash or the same verifier failure twice) escalates instead of looping.
/// </summary>
public class RecoverEdgesTests
{
    private static async Task<AgentRun> SeedRecoverAsync(AgentTestHost host, string recoveryReason, int planRevision = 0, int stepIndex = 0, int replans = 3, int retries = 2)
    {
        var run = await host.CreateRunAsync();
        await host.SaveAsync(async db =>
        {
            var tracked = await db.AgentRuns.FirstAsync(r => r.Id == run.Id);
            tracked.Stage = AgentStage.Recover;
            tracked.StatusReason = "verifier said no";
            tracked.RecoveryReason = recoveryReason;
            tracked.PlanRevision = planRevision;
            tracked.CurrentStepIndex = stepIndex;
            tracked.Budget.ReplansLeft = replans;
            tracked.Budget.RetriesLeft = retries;
        });
        return run;
    }

    private static Task SeedPlanAsync(AgentTestHost host, Guid runId, int revision, string hash) => host.SaveAsync(db =>
    {
        db.AgentPlans.Add(new AgentPlan
        {
            RunId = runId,
            Revision = revision,
            Hash = hash,
            Goal = "g",
            Steps = [new PlanStep { Index = 0, Description = "d", Tool = null, SuccessCriteria = "s" }],
            CreatedAt = DateTime.UtcNow.AddSeconds(revision),
        });
        return Task.CompletedTask;
    });

    private static Task SeedFailedStepAsync(AgentTestHost host, Guid runId, int attempt, string verifierMessage, int revision = 0, int index = 0) => host.SaveAsync(db =>
    {
        db.AgentSteps.Add(new AgentStep
        {
            RunId = runId,
            PlanRevision = revision,
            PlanStepIndex = index,
            Attempt = attempt,
            Status = AgentStep.StatusFailed,
            VerifierMessage = verifierMessage,
            StartedAt = DateTime.UtcNow.AddSeconds(attempt),
            CompletedAt = DateTime.UtcNow.AddSeconds(attempt),
        });
        return Task.CompletedTask;
    });

    private static readonly string HashA = new('A', 64);
    private static readonly string HashB = new('B', 64);

    // ── Each edge spends exactly its own counter ─────────────────────────────────────────

    [Fact]
    public async Task Retry_edge_spends_one_retry_and_nothing_else()
    {
        using var host = new AgentTestHost();
        var run = await SeedRecoverAsync(host, RecoveryReasons.StepFailed, stepIndex: 1);
        await SeedPlanAsync(host, run.Id, 0, HashA);
        await SeedFailedStepAsync(host, run.Id, 1, "first failure", index: 1);

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(AgentStage.Act);
        stepped.Budget.RetriesLeft.Should().Be(1);
        stepped.Budget.ReplansLeft.Should().Be(3);
        stepped.PlanRevision.Should().Be(0);
        stepped.CurrentStepIndex.Should().Be(1);
    }

    [Fact]
    public async Task Replan_edge_spends_one_replan_increments_the_revision_and_resets_the_step()
    {
        using var host = new AgentTestHost();
        var run = await SeedRecoverAsync(host, RecoveryReasons.StepFailed, stepIndex: 2, retries: 0);

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(AgentStage.Plan);
        stepped.Budget.ReplansLeft.Should().Be(2);
        stepped.Budget.RetriesLeft.Should().Be(0);
        stepped.PlanRevision.Should().Be(1);
        stepped.CurrentStepIndex.Should().Be(0);
    }

    [Fact]
    public async Task Policy_denial_takes_the_replan_edge_even_with_retries_left()
    {
        using var host = new AgentTestHost();
        var run = await SeedRecoverAsync(host, RecoveryReasons.PolicyDenied, stepIndex: 1, retries: 2);

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(AgentStage.Plan);
        stepped.PlanRevision.Should().Be(1);
        stepped.Budget.ReplansLeft.Should().Be(2);
        stepped.Budget.RetriesLeft.Should().Be(2, "a denied call is never retried");
    }

    [Fact]
    public async Task Escalation_edge_spends_nothing_and_creates_an_escalation()
    {
        using var host = new AgentTestHost();
        var run = await SeedRecoverAsync(host, RecoveryReasons.PolicyDenied, replans: 0, retries: 2);

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(AgentStage.Failed);
        stepped.Budget.ReplansLeft.Should().Be(0);
        stepped.Budget.RetriesLeft.Should().Be(2);
        (await host.QueryAsync(db => db.AIEscalations.CountAsync(e => e.AgentRunId == run.Id))).Should().Be(1);
    }

    // ── Oscillation detection (plan section 2.3) ─────────────────────────────────────────

    [Fact]
    public async Task A_plan_identical_to_one_of_the_last_three_revisions_escalates()
    {
        using var host = new AgentTestHost();
        var run = await SeedRecoverAsync(host, RecoveryReasons.StepFailed, planRevision: 3);
        await SeedPlanAsync(host, run.Id, 0, HashA);
        await SeedPlanAsync(host, run.Id, 1, HashB);
        await SeedPlanAsync(host, run.Id, 2, new string('C', 64));
        await SeedPlanAsync(host, run.Id, 3, HashA);   // revision 3 repeats revision 0

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(AgentStage.Failed);
        stepped.StatusReason.Should().Contain("Oscillation detected").And.Contain("revision 3 is identical to revision 0");
        stepped.Budget.RetriesLeft.Should().Be(2, "oscillation is detected before any counter is spent");
        stepped.Budget.ReplansLeft.Should().Be(3);
        var escalation = await host.QueryAsync(db => db.AIEscalations.SingleAsync(e => e.AgentRunId == run.Id));
        escalation.EscalationReason.Should().Contain("Oscillation");
    }

    [Fact]
    public async Task A_plan_matching_only_a_revision_older_than_three_back_is_not_oscillation()
    {
        using var host = new AgentTestHost();
        var run = await SeedRecoverAsync(host, RecoveryReasons.StepFailed, planRevision: 4);
        await SeedPlanAsync(host, run.Id, 0, HashA);
        await SeedPlanAsync(host, run.Id, 1, HashB);
        await SeedPlanAsync(host, run.Id, 2, new string('C', 64));
        await SeedPlanAsync(host, run.Id, 3, new string('D', 64));
        await SeedPlanAsync(host, run.Id, 4, HashA);   // matches revision 0, which is outside the window

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(AgentStage.Act, "the run retries normally");
    }

    [Fact]
    public async Task A_step_failing_twice_with_the_same_verifier_message_escalates()
    {
        using var host = new AgentTestHost();
        var run = await SeedRecoverAsync(host, RecoveryReasons.StepFailed, stepIndex: 0, retries: 2);
        await SeedPlanAsync(host, run.Id, 0, HashA);
        await SeedFailedStepAsync(host, run.Id, 1, "Tool 'x' failed: boom");
        await SeedFailedStepAsync(host, run.Id, 2, "Tool 'x' failed: boom");

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(AgentStage.Failed);
        stepped.StatusReason.Should().Contain("failed twice with the same verifier message");
        stepped.Budget.RetriesLeft.Should().Be(2);
        (await host.QueryAsync(db => db.AIEscalations.CountAsync(e => e.AgentRunId == run.Id))).Should().Be(1);
    }

    [Fact]
    public async Task A_step_failing_twice_with_different_messages_is_retried()
    {
        using var host = new AgentTestHost();
        var run = await SeedRecoverAsync(host, RecoveryReasons.StepFailed, stepIndex: 0, retries: 2);
        await SeedPlanAsync(host, run.Id, 0, HashA);
        await SeedFailedStepAsync(host, run.Id, 1, "Tool 'x' failed: boom");
        await SeedFailedStepAsync(host, run.Id, 2, "Tool 'x' failed: different");

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(AgentStage.Act);
        stepped.Budget.RetriesLeft.Should().Be(1);
    }

    [Fact]
    public async Task End_to_end_a_planner_that_repeats_its_plan_after_a_failure_is_stopped_as_oscillation()
    {
        var tool = new StubTool("update_notes", Intelligently.Core.Agents.Tools.ToolRiskLevel.Mutating,
            execute: call => Intelligently.Core.Agents.Tools.ToolResult.Error(call, "always fails"));
        using var host = new AgentTestHost(s => s.UseTools(tool));
        EngineStubs.PlanReturns(host.Engine, StubTool.PlanFor("update_notes"));      // same plan every time
        EngineStubs.DecideReturns(host.Engine, StubTool.Call("update_notes", 7));

        var run = await host.CreateRunAsync();
        var (final, stages) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusFailed);
        final.StatusReason.Should().Contain("Oscillation detected");
        // attempt 1 fails → Recover: same-message check needs two failures → retry (identical message)
        // attempt 2 fails → Recover: two identical verifier messages → escalate. Nothing replans.
        final.PlanRevision.Should().Be(0);
        final.Budget.RetriesLeft.Should().Be(1);
        final.Budget.ReplansLeft.Should().Be(3);
        stages.Should().EndWith(new[] { AgentStage.Verify, AgentStage.Recover, AgentStage.Failed });
        (await host.QueryAsync(db => db.AIEscalations.CountAsync(e => e.AgentRunId == run.Id))).Should().Be(1);
    }

    [Fact]
    public async Task End_to_end_a_repeated_plan_revision_is_stopped_as_oscillation()
    {
        // Each attempt fails with a different message (so the same-message check never fires), the planner
        // always returns the same plan, so revision 1 hashes like revision 0 and Recover escalates.
        var attempt = 0;
        var tool = new StubTool("update_notes", Intelligently.Core.Agents.Tools.ToolRiskLevel.Mutating,
            execute: call => Intelligently.Core.Agents.Tools.ToolResult.Error(call, $"failure {++attempt}"));
        using var host = new AgentTestHost(s => s.UseTools(tool));
        EngineStubs.PlanReturns(host.Engine, StubTool.PlanFor("update_notes"));
        var turn = 0;
        host.Engine.Setup(e => e.DecideAsync(It.IsAny<Intelligently.Core.Agents.Contracts.PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => EngineStubs.Decision(StubTool.Call("update_notes", 7, text: $"t{++turn}", id: $"toolu_{turn}")));

        var run = await host.CreateRunAsync();
        var (final, stages) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusFailed);
        final.StatusReason.Should().Contain("revision 1 is identical to revision 0");
        final.PlanRevision.Should().Be(1);
        final.Budget.RetriesLeft.Should().Be(0);
        final.Budget.ReplansLeft.Should().Be(2);
        stages.Count(s => s == AgentStage.Plan).Should().Be(2);
    }
}
