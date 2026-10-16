using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>Every stage is committed, so a new process picks the run up exactly where the old one stopped.</summary>
public class ResumeAfterCrashTests
{
    [Fact]
    public async Task A_restarted_host_continues_from_the_last_committed_stage()
    {
        using var host = new AgentTestHost();
        EngineStubs.PlanReturns(host.Engine, EngineStubs.LookupPlan());
        EngineStubs.DecideReturns(host.Engine, EngineStubs.LookupCall());

        var run = await host.CreateRunAsync();
        (await host.StepAsync(run.Id)).Stage.Should().Be(AgentStage.Plan);     // Interpret → Plan
        (await host.StepAsync(run.Id)).Stage.Should().Be(AgentStage.Act);      // Plan → Act (plan revision 0 committed)

        // "Crash": the container (and every scoped DbContext) is gone; only the database survives.
        host.Restart();

        var persisted = await host.LoadAsync(run.Id);
        persisted.Stage.Should().Be(AgentStage.Act);
        persisted.Status.Should().Be(AgentRun.StatusRunning);
        (await host.QueryAsync(db => db.AgentPlans.CountAsync(p => p.RunId == run.Id))).Should().Be(1);

        var resumed = await host.StepAsync(run.Id);                            // Act → Observe on the new host

        resumed.Stage.Should().Be(AgentStage.Observe);
        host.Engine.Verify(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()), Times.Once,
            "the plan was persisted before the crash and must not be regenerated");
        host.Engine.Verify(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);

        var (final, stages) = await host.RunToEndAsync(run.Id);
        final.Status.Should().Be(AgentRun.StatusCompleted);
        stages.Should().Equal(AgentStage.Observe, AgentStage.Verify, AgentStage.Learn, AgentStage.Completed);
    }

    [Fact]
    public async Task A_crash_mid_stage_leaves_the_run_at_that_stage_for_the_next_host()
    {
        using var host = new AgentTestHost();
        EngineStubs.PlanReturns(host.Engine, EngineStubs.LookupPlan());
        // First host: the model call during Act dies before the stage can commit.
        host.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("process terminated"));

        var run = await host.CreateRunAsync();
        await host.StepAsync(run.Id);
        await host.StepAsync(run.Id);
        var act = () => host.StepAsync(run.Id);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var persisted = await host.LoadAsync(run.Id);
        persisted.Stage.Should().Be(AgentStage.Act, "cancellation is not a handler failure; nothing was committed");
        persisted.Status.Should().Be(AgentRun.StatusRunning);
        (await host.QueryAsync(db => db.AgentSteps.CountAsync(s => s.RunId == run.Id))).Should().Be(0);

        // Second host: the same stage runs again and succeeds.
        host.Restart();
        EngineStubs.DecideReturns(host.Engine, EngineStubs.LookupCall());

        var (final, _) = await host.RunToEndAsync(run.Id);
        final.Status.Should().Be(AgentRun.StatusCompleted);
        (await host.QueryAsync(db => db.AgentSteps.CountAsync(s => s.RunId == run.Id))).Should().Be(1);
    }
}
