using FluentAssertions;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Agents.StateGraph.Handlers;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.UnitTests.Agents;

/// <summary>Recover spends exactly one counter per visit and escalates when nothing is left.</summary>
public class RecoverHandlerTests
{
    private static async Task<AgentRun> SeedRecoverAsync(AgentTestHost host, string recoveryReason, int replans, int retries, int tokens = 1000)
    {
        var run = await host.CreateRunAsync();
        await host.SaveAsync(async db =>
        {
            var tracked = await db.AgentRuns.FirstAsync(r => r.Id == run.Id);
            tracked.Stage = AgentStage.Recover;
            tracked.StatusReason = "verifier said no";
            tracked.RecoveryReason = recoveryReason;
            tracked.CurrentStepIndex = 2;
            tracked.Budget.ReplansLeft = replans;
            tracked.Budget.RetriesLeft = retries;
            tracked.Budget.TokensLeft = tokens;
        });
        return run;
    }

    [Fact]
    public async Task Step_failure_with_retries_left_decrements_retries_and_returns_to_Act()
    {
        using var host = new AgentTestHost();
        var run = await SeedRecoverAsync(host, RecoveryReasons.StepFailed, replans: 3, retries: 2);

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(AgentStage.Act);
        var persisted = await host.LoadAsync(run.Id);
        persisted.Budget.RetriesLeft.Should().Be(1);
        persisted.Budget.ReplansLeft.Should().Be(3);
        persisted.CurrentStepIndex.Should().Be(2, "a retry resumes the same step");
        persisted.PlanRevision.Should().Be(0);
    }

    [Fact]
    public async Task Step_failure_without_retries_decrements_replans_and_returns_to_Plan()
    {
        using var host = new AgentTestHost();
        var run = await SeedRecoverAsync(host, RecoveryReasons.StepFailed, replans: 3, retries: 0);

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(AgentStage.Plan);
        var persisted = await host.LoadAsync(run.Id);
        persisted.Budget.ReplansLeft.Should().Be(2);
        persisted.PlanRevision.Should().Be(1);
        persisted.CurrentStepIndex.Should().Be(0);
    }

    [Fact]
    public async Task Plan_failure_decrements_replans_not_retries()
    {
        using var host = new AgentTestHost();
        var run = await SeedRecoverAsync(host, RecoveryReasons.PlanFailed, replans: 1, retries: 2);

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(AgentStage.Plan);
        var persisted = await host.LoadAsync(run.Id);
        persisted.Budget.ReplansLeft.Should().Be(0);
        persisted.Budget.RetriesLeft.Should().Be(2);
    }

    [Fact]
    public async Task Exhausted_budget_escalates_to_an_instructor_and_fails_the_run()
    {
        using var host = new AgentTestHost();
        var run = await SeedRecoverAsync(host, RecoveryReasons.StepFailed, replans: 0, retries: 0);

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(AgentStage.Failed);
        stepped.Status.Should().Be(AgentRun.StatusFailed);
        stepped.StatusReason.Should().Contain("Escalated");

        var escalation = await host.QueryAsync(db => db.AIEscalations.SingleAsync(e => e.AgentRunId == run.Id));
        escalation.ConversationId.Should().BeNull();
        escalation.UserId.Should().Be(run.UserId);
        escalation.CourseId.Should().Be(run.CourseId);
        escalation.Priority.Should().Be("High");
        escalation.Status.Should().Be("New");
        escalation.EscalationReason.Should().Contain("could not recover");

        var traces = await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id).ToListAsync());
        traces.Should().Contain(t => t.Kind == AgentTrace.KindEscalation);
    }

    [Fact]
    public async Task Exhausted_hard_budget_escalates_even_when_replans_remain()
    {
        using var host = new AgentTestHost();
        var run = await SeedRecoverAsync(host, RecoveryReasons.StepFailed, replans: 3, retries: 2, tokens: 0);

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(AgentStage.Failed);
        (await host.QueryAsync(db => db.AIEscalations.CountAsync(e => e.AgentRunId == run.Id))).Should().Be(1);
    }
}
