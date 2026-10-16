using FluentAssertions;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// A stage handler only runs under a database lease held by one worker at a time: concurrent steps
/// execute the handler once, an expired lease can be taken over, and the claim is released however
/// the stage ends.
/// </summary>
public class RunLeaseTests
{
    private static AgentTestHost HostWith(params IStageHandler[] handlers) => new(services =>
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IStageHandler)).ToList())
            services.Remove(descriptor);
        foreach (var handler in handlers)
            services.AddSingleton(handler);
    });

    private static Task SetLeaseAsync(AgentTestHost host, Guid runId, string? claimedBy, DateTime? until) => host.SaveAsync(async db =>
    {
        var run = await db.AgentRuns.FirstAsync(r => r.Id == runId);
        run.ClaimedBy = claimedBy;
        run.ClaimedUntil = until;
    });

    [Fact]
    public async Task Two_concurrent_steps_on_the_same_run_execute_the_handler_once()
    {
        var handler = new BlockingHandler(AgentStage.Interpret, AgentStage.Plan);
        using var host = HostWith(handler);
        var run = await host.CreateRunAsync();

        var first = host.TryStepAsync(run.Id, "worker-a");
        await handler.Entered.Task;                                     // worker-a is inside the handler, holding the lease

        var second = await host.TryStepAsync(run.Id, "worker-b");       // worker-b tries the same stage meanwhile

        second.Executed.Should().BeFalse();
        second.Run.Stage.Should().Be(AgentStage.Interpret);
        second.Run.ClaimedBy.Should().Be("worker-a");
        handler.Calls.Should().Be(1, "the second worker must not enter the handler");

        handler.Release.SetResult();
        var result = await first;

        result.Executed.Should().BeTrue();
        result.Run.Stage.Should().Be(AgentStage.Plan);
        handler.Calls.Should().Be(1);
        var persisted = await host.LoadAsync(run.Id);
        persisted.Stage.Should().Be(AgentStage.Plan);
        persisted.ClaimedBy.Should().BeNull();
        (await host.QueryAsync(db => db.AgentTraces.CountAsync(t => t.RunId == run.Id && t.Kind == AgentTrace.KindTransition))).Should().Be(1);
    }

    [Fact]
    public async Task A_live_lease_held_by_another_worker_blocks_the_step()
    {
        var handler = new StubHandler(AgentStage.Interpret, AgentStage.Plan);
        using var host = HostWith(handler);
        var run = await host.CreateRunAsync();
        var until = DateTime.UtcNow.AddMinutes(10);
        await SetLeaseAsync(host, run.Id, "other-host/1", until);

        var result = await host.TryStepAsync(run.Id, "this-host/2");

        result.Executed.Should().BeFalse();
        handler.Calls.Should().Be(0);
        var persisted = await host.LoadAsync(run.Id);
        persisted.Stage.Should().Be(AgentStage.Interpret);
        persisted.ClaimedBy.Should().Be("other-host/1", "a failed claim must not touch the lease");
        persisted.ClaimedUntil.Should().Be(until);
    }

    [Fact]
    public async Task An_expired_lease_is_taken_over()
    {
        string? claimedByDuringStage = null;
        var handler = new StubHandler(AgentStage.Interpret, r =>
        {
            claimedByDuringStage = r.ClaimedBy;
            return StageOutcome.To(AgentStage.Plan, "reclaimed");
        });
        using var host = HostWith(handler);
        var run = await host.CreateRunAsync();
        await SetLeaseAsync(host, run.Id, "crashed-host/9", DateTime.UtcNow.AddMinutes(-1));

        var result = await host.TryStepAsync(run.Id, "this-host/2");

        result.Executed.Should().BeTrue();
        result.Run.Stage.Should().Be(AgentStage.Plan);
        claimedByDuringStage.Should().Be("this-host/2");
        handler.Calls.Should().Be(1);
        var persisted = await host.LoadAsync(run.Id);
        persisted.ClaimedBy.Should().BeNull();
        persisted.ClaimedUntil.Should().BeNull();
    }

    [Fact]
    public async Task The_claim_is_held_during_the_stage_and_released_by_the_commit()
    {
        var handler = new BlockingHandler(AgentStage.Interpret, AgentStage.Plan);
        using var host = HostWith(handler);
        var run = await host.CreateRunAsync();
        var before = DateTime.UtcNow;

        var step = host.StepAsync(run.Id);
        await handler.Entered.Task;

        var during = await host.LoadAsync(run.Id);
        during.ClaimedBy.Should().Be(host.WorkerId);
        during.ClaimedUntil.Should().NotBeNull();
        during.ClaimedUntil!.Value.Should().BeAfter(before.AddMinutes(5), "the default lease is longer than any stage");

        handler.Release.SetResult();
        await step;

        var after = await host.LoadAsync(run.Id);
        after.Stage.Should().Be(AgentStage.Plan);
        after.ClaimedBy.Should().BeNull();
        after.ClaimedUntil.Should().BeNull();
    }

    [Fact]
    public async Task The_claim_is_released_after_a_handler_failure()
    {
        using var host = HostWith(new StubHandler(AgentStage.Interpret, _ => throw new InvalidOperationException("boom")));
        var run = await host.CreateRunAsync();

        var act = () => host.TryStepAsync(run.Id, "worker-a");
        await act.Should().ThrowAsync<InvalidOperationException>();

        var persisted = await host.LoadAsync(run.Id);
        persisted.Status.Should().Be(AgentRun.StatusFailed);
        persisted.ClaimedBy.Should().BeNull();
        persisted.ClaimedUntil.Should().BeNull();
    }

    [Fact]
    public async Task The_claim_is_released_when_the_stage_is_cancelled_before_it_commits()
    {
        using var host = HostWith(new StubHandler(AgentStage.Interpret, _ => throw new OperationCanceledException("shutting down")));
        var run = await host.CreateRunAsync();

        var act = () => host.TryStepAsync(run.Id, "worker-a");
        await act.Should().ThrowAsync<OperationCanceledException>();

        var persisted = await host.LoadAsync(run.Id);
        persisted.Stage.Should().Be(AgentStage.Interpret, "nothing was committed");
        persisted.Status.Should().Be(AgentRun.StatusRunning);
        persisted.ClaimedBy.Should().BeNull("another worker must be able to take the run at once");
        persisted.ClaimedUntil.Should().BeNull();
    }

    [Fact]
    public async Task A_worker_whose_lease_was_taken_over_cannot_commit_its_stale_stage()
    {
        var handler = new BlockingHandler(AgentStage.Interpret, AgentStage.Plan, blockFirstCallOnly: true);
        using var host = HostWith(handler);
        var run = await host.CreateRunAsync();

        var slow = host.TryStepAsync(run.Id, "slow-worker");
        await handler.Entered.Task;

        // The slow worker's lease runs out while it is still inside the handler, and another worker takes the run over.
        await SetLeaseAsync(host, run.Id, "slow-worker", DateTime.UtcNow.AddMinutes(-1));
        var takeover = await host.TryStepAsync(run.Id, "fresh-worker");
        takeover.Executed.Should().BeTrue();
        takeover.Run.Stage.Should().Be(AgentStage.Plan);

        handler.Release.SetResult();
        var act = () => slow;
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>("the stale stage must not be committed twice");

        var persisted = await host.LoadAsync(run.Id);
        persisted.Stage.Should().Be(AgentStage.Plan);
        persisted.ClaimedBy.Should().BeNull("the fresh worker's commit released the lease and the stale release must not clear someone else's");
        (await host.QueryAsync(db => db.AgentTraces.CountAsync(t => t.RunId == run.Id && t.Kind == AgentTrace.KindTransition))).Should().Be(1);
    }

    [Fact]
    public async Task A_duplicate_enqueue_of_a_finished_run_is_not_claimed()
    {
        var handler = new StubHandler(AgentStage.Completed, AgentStage.Act);
        using var host = HostWith(handler);
        var run = await host.CreateRunAsync();
        await host.SaveAsync(async db =>
        {
            var tracked = await db.AgentRuns.FirstAsync(r => r.Id == run.Id);
            tracked.Stage = AgentStage.Completed;
            tracked.Status = AgentRun.StatusCompleted;
        });

        var result = await host.TryStepAsync(run.Id, "worker-a");

        result.Executed.Should().BeFalse();
        result.Run.IsActive.Should().BeFalse();
        handler.Calls.Should().Be(0);
        (await host.LoadAsync(run.Id)).ClaimedBy.Should().BeNull();
    }

    /// <summary>Signals when it is entered and waits to be released, so a test can act while a stage is in flight.</summary>
    private sealed class BlockingHandler : IStageHandler
    {
        private readonly AgentStage _next;
        private readonly bool _blockFirstCallOnly;

        public BlockingHandler(AgentStage stage, AgentStage next, bool blockFirstCallOnly = false)
        {
            Stage = stage;
            _next = next;
            _blockFirstCallOnly = blockFirstCallOnly;
        }

        public AgentStage Stage { get; }
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<StageOutcome> ExecuteAsync(AgentRun run, CancellationToken ct = default)
        {
            Calls++;
            if (Calls == 1 || !_blockFirstCallOnly)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return StageOutcome.To(_next, "blocked then released");
        }
    }
}
