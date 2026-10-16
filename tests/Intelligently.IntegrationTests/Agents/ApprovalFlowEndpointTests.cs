using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Intelligently.IntegrationTests.Agents;

/// <summary>
/// The approval flow end to end in the Web host: the worker parks a run on a Destructive call,
/// an instructor's decision through the approval gate re-enqueues it, and the worker finishes it.
/// </summary>
public class ApprovalFlowEndpointTests : IClassFixture<AgentWebFactory>
{
    private static readonly ModelUsage Usage = new("claude-opus-5-5", 100, 20);
    private readonly AgentWebFactory _factory;

    public ApprovalFlowEndpointTests(AgentWebFactory factory)
    {
        _factory = factory;
    }

    private void StubDestructivePlan()
    {
        _factory.Engine.Reset();
        _factory.Engine.Setup(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanResult(new AgentPlan
            {
                Goal = "Remove the learner's stale note.",
                Steps = [new PlanStep { Index = 0, Description = "Delete note 42.", Tool = StubDestructiveTool.ToolName, SuccessCriteria = "The note is gone." }],
            }, Usage, "{}"));
        _factory.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                [new ToolCall("toolu_del", StubDestructiveTool.ToolName, JsonSerializer.SerializeToElement(new { course_id = 7, note_id = 42 }))],
                "Deleting the note.", "tool_use", Usage, "{}"));
    }

    private static async Task<string> PollUntilAsync(HttpClient client, Guid id, string status, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string html;
        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/agent/runs/{id}");
            request.Headers.Add("HX-Request", "true");
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            html = await response.Content.ReadAsStringAsync();
            if (Regex.IsMatch(html, $"data-status=\"{status}\"")) return html;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);

        return html;
    }

    private async Task<Guid> CreateRunAsync(HttpClient client, string userId)
    {
        var created = await client.PostAsJsonAsync("/agent/runs", new
        {
            userId,
            courseId = 7,
            taskType = "NoteCleanup",
            taskSpec = new { noteId = 42 },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Destructive_call_parks_the_run_and_approving_it_lets_the_worker_finish()
    {
        StubDestructivePlan();
        var client = _factory.CreateClient();
        var before = _factory.DestructiveTool.Executions;

        var id = await CreateRunAsync(client, "learner-approve");

        var waiting = await PollUntilAsync(client, id, AgentRun.StatusWaiting, TimeSpan.FromSeconds(30));
        waiting.Should().Contain("data-stage=\"AwaitingApproval\"");
        waiting.Should().Contain("Waiting for an instructor");
        waiting.Should().Contain("hx-trigger", "a parked run keeps polling so the decision shows up");

        AgentApproval approval = null!;
        await _factory.WithDbAsync(async db =>
        {
            approval = await db.AgentApprovals.SingleAsync(a => a.RunId == id);
            approval.Status.Should().Be(AgentApproval.StatusPending);
            approval.ToolName.Should().Be(StubDestructiveTool.ToolName);
            (await db.AIEscalations.CountAsync(e => e.AgentRunId == id && e.Status == "New")).Should().Be(1);
            (await db.AgentRuns.SingleAsync(r => r.Id == id)).Principal.UserId.Should().Be("learner-approve");
        });
        _factory.DestructiveTool.Executions.Should().Be(before);

        // An instructor decides (what the Admin /approvals page does); the gate re-enqueues the run.
        using (var scope = _factory.Services.CreateScope())
        {
            var gate = scope.ServiceProvider.GetRequiredService<IApprovalGate>();
            var decided = await gate.DecideAsync(approval.Id, approved: true, decidedBy: "instructor-1", note: "Go ahead.");
            decided.Status.Should().Be(AgentApproval.StatusApproved);
        }

        var completed = await PollUntilAsync(client, id, AgentRun.StatusCompleted, TimeSpan.FromSeconds(30));
        completed.Should().Contain("data-stage=\"Completed\"");
        completed.Should().NotContain("hx-trigger");
        _factory.DestructiveTool.Executions.Should().Be(before + 1);

        await _factory.WithDbAsync(async db =>
        {
            var steps = await db.AgentSteps.Where(s => s.RunId == id).ToListAsync();
            steps.Should().ContainSingle().Which.Status.Should().Be(AgentStep.StatusPassed);
            (await db.AIEscalations.SingleAsync(e => e.AgentRunId == id)).Status.Should().Be("Resolved");
            var transitions = await db.AgentTraces.Where(t => t.RunId == id && t.Kind == AgentTrace.KindTransition).OrderBy(t => t.Id).Select(t => t.Stage).ToListAsync();
            transitions.Should().Equal(
                AgentStage.Interpret, AgentStage.Plan, AgentStage.Act,
                AgentStage.AwaitingApproval, AgentStage.Act, AgentStage.Observe, AgentStage.Verify, AgentStage.Learn);
        });
        _factory.Engine.Verify(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Denying_the_call_sends_the_run_through_Recover_to_a_new_plan_revision()
    {
        StubDestructivePlan();
        var client = _factory.CreateClient();
        var before = _factory.DestructiveTool.Executions;

        var id = await CreateRunAsync(client, "learner-deny");
        await PollUntilAsync(client, id, AgentRun.StatusWaiting, TimeSpan.FromSeconds(30));

        Guid approvalId = Guid.Empty;
        await _factory.WithDbAsync(async db => approvalId = (await db.AgentApprovals.SingleAsync(a => a.RunId == id)).Id);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IApprovalGate>().DecideAsync(approvalId, approved: false, decidedBy: "instructor-1", note: "Not allowed.");

        // The planner keeps proposing the same destructive step, so the run parks again on revision 1.
        var parkedAgain = await PollUntilAsync(client, id, AgentRun.StatusWaiting, TimeSpan.FromSeconds(30));
        parkedAgain.Should().Contain("data-stage=\"AwaitingApproval\"");

        await _factory.WithDbAsync(async db =>
        {
            var run = await db.AgentRuns.SingleAsync(r => r.Id == id);
            run.PlanRevision.Should().Be(1);
            run.Budget.ReplansLeft.Should().Be(2);
            run.Budget.RetriesLeft.Should().Be(2);
            (await db.AgentApprovals.CountAsync(a => a.RunId == id)).Should().Be(2);
            var transitions = await db.AgentTraces.Where(t => t.RunId == id && t.Kind == AgentTrace.KindTransition).OrderBy(t => t.Id).Select(t => t.Stage).ToListAsync();
            transitions.Should().Equal(
                AgentStage.Interpret, AgentStage.Plan, AgentStage.Act,
                AgentStage.AwaitingApproval, AgentStage.Recover, AgentStage.Plan, AgentStage.Act);
        });
        _factory.DestructiveTool.Executions.Should().Be(before);
    }
}
