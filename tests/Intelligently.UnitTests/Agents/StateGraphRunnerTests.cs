using FluentAssertions;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Intelligently.UnitTests.Agents;

/// <summary>Runner behaviour with stub handlers: every table row commits, anything else is rejected.</summary>
public class StateGraphRunnerTests
{
    private static AgentTestHost HostWith(params StubHandler[] handlers) => new(services =>
    {
        // Replace the real handlers with stubs so each edge can be exercised directly.
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IStageHandler)).ToList())
            services.Remove(descriptor);
        foreach (var handler in handlers)
            services.AddSingleton<IStageHandler>(handler);
    });

    private static async Task<AgentRun> SeedAsync(AgentTestHost host, AgentStage stage, string status = AgentRun.StatusRunning)
    {
        var run = await host.CreateRunAsync();
        await host.SaveAsync(async db =>
        {
            var tracked = await db.AgentRuns.FirstAsync(r => r.Id == run.Id);
            tracked.Stage = stage;
            tracked.Status = status;
        });
        return run;
    }

    [Theory]
    [MemberData(nameof(StageTransitionsTests.TableRows), MemberType = typeof(StageTransitionsTests))]
    public async Task Runner_commits_every_row_of_the_transition_table(AgentStage from, AgentStage to)
    {
        var handler = new StubHandler(from, to, "stubbed");
        using var host = HostWith(handler);
        var run = await SeedAsync(host, from);

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(to);
        stepped.StatusReason.Should().Be("stubbed");
        handler.Calls.Should().Be(1);

        var persisted = await host.LoadAsync(run.Id);
        persisted.Stage.Should().Be(to);
        persisted.Status.Should().Be(to switch
        {
            AgentStage.Completed => AgentRun.StatusCompleted,
            AgentStage.Failed => AgentRun.StatusFailed,
            AgentStage.AwaitingApproval => AgentRun.StatusWaiting,
            _ => AgentRun.StatusRunning,
        });
        (persisted.CompletedAt is not null).Should().Be(StageTransitions.IsTerminal(to));

        var traces = await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id).ToListAsync());
        traces.Should().ContainSingle(t => t.Kind == AgentTrace.KindTransition && t.Stage == from);
    }

    [Fact]
    public async Task Illegal_transition_is_rejected_and_the_run_is_failed()
    {
        using var host = HostWith(new StubHandler(AgentStage.Interpret, AgentStage.Learn));
        var run = await host.CreateRunAsync();

        var act = () => host.StepAsync(run.Id);

        var ex = await act.Should().ThrowAsync<IllegalStageTransitionException>();
        ex.Which.From.Should().Be(AgentStage.Interpret);
        ex.Which.To.Should().Be(AgentStage.Learn);

        var persisted = await host.LoadAsync(run.Id);
        persisted.Stage.Should().Be(AgentStage.Failed);
        persisted.Status.Should().Be(AgentRun.StatusFailed);
        persisted.StatusReason.Should().Contain("Interpret → Learn");
    }

    [Fact]
    public async Task Handler_exception_fails_the_run_and_propagates()
    {
        using var host = HostWith(new StubHandler(AgentStage.Interpret, _ => throw new InvalidOperationException("boom")));
        var run = await host.CreateRunAsync();

        var act = () => host.StepAsync(run.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        var persisted = await host.LoadAsync(run.Id);
        persisted.Status.Should().Be(AgentRun.StatusFailed);
        persisted.StatusReason.Should().Contain("boom");
    }

    [Theory]
    [InlineData(AgentStage.AwaitingApproval, AgentRun.StatusWaiting)]
    [InlineData(AgentStage.Completed, AgentRun.StatusCompleted)]
    [InlineData(AgentStage.Failed, AgentRun.StatusFailed)]
    public async Task Runs_that_are_not_running_are_returned_untouched(AgentStage stage, string status)
    {
        var handler = new StubHandler(stage, AgentStage.Act);
        using var host = HostWith(handler);
        var run = await SeedAsync(host, stage, status);

        var stepped = await host.StepAsync(run.Id);

        stepped.Stage.Should().Be(stage);
        stepped.Status.Should().Be(status);
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_run_throws()
    {
        using var host = HostWith();
        var act = () => host.StepAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Each_commit_rotates_the_concurrency_stamp()
    {
        using var host = HostWith(new StubHandler(AgentStage.Interpret, AgentStage.Plan));
        var run = await host.CreateRunAsync();
        var before = (await host.LoadAsync(run.Id)).ConcurrencyStamp;

        await host.StepAsync(run.Id);

        (await host.LoadAsync(run.Id)).ConcurrencyStamp.Should().NotBe(before);
    }
}
