using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Entities.AI;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.IntegrationTests.Admin;

/// <summary>The Admin approval queue lists pending calls and records decisions; the run page shows steps and traces.</summary>
public class AdminPagesTests : IClassFixture<AdminWebFactory>
{
    private readonly AdminWebFactory _factory;

    public AdminPagesTests(AdminWebFactory factory)
    {
        _factory = factory;
    }

    /// <summary>A run parked at AwaitingApproval with one pending approval, as the Web worker would leave it.</summary>
    private async Task<(AgentRun Run, AgentStep Step, AgentApproval Approval)> SeedParkedRunAsync(string userId)
    {
        var run = new AgentRun
        {
            UserId = userId,
            CourseId = 7,
            TaskType = "NoteCleanup",
            TaskSpec = """{"noteId":42}""",
            Stage = AgentStage.AwaitingApproval,
            Status = AgentRun.StatusWaiting,
            StatusReason = "Tool 'delete_notes' requires approval.",
            Principal = RunPrincipal.Learner(userId, 7),
        };
        var step = new AgentStep
        {
            RunId = run.Id,
            PlanRevision = 0,
            PlanStepIndex = 0,
            Attempt = 1,
            Status = AgentStep.StatusAwaitingApproval,
            ToolCallsJson = JsonSerializer.Serialize(new[] { new ToolCall("toolu_del", "delete_notes", JsonSerializer.SerializeToElement(new { course_id = 7, note_id = 42 })) }),
            ToolResultsJson = "[]",
        };
        var escalation = new AIEscalation { AgentRunId = run.Id, UserId = userId, CourseId = 7, LearnerQuestion = run.TaskSpec, EscalationReason = "needs approval", Priority = "Medium", Status = "New" };
        var approval = new AgentApproval
        {
            RunId = run.Id,
            StepId = step.Id,
            ToolName = "delete_notes",
            ToolCallJson = JsonSerializer.Serialize(new ToolCall("toolu_del", "delete_notes", JsonSerializer.SerializeToElement(new { course_id = 7, note_id = 42 }))),
            ArgsHash = new string('A', 64),
            Reason = "Rule 'destructive-approval': Destructive tool 'delete_notes' always requires approval.",
            Escalation = escalation,
        };
        var plan = new AgentPlan
        {
            RunId = run.Id,
            Revision = 0,
            Hash = new string('B', 64),
            Goal = "Remove the stale note.",
            Steps = [new PlanStep { Index = 0, Description = "Delete note 42.", Tool = "delete_notes", SuccessCriteria = "Gone." }],
        };
        var trace = new AgentTrace { RunId = run.Id, Stage = AgentStage.Act, Kind = AgentTrace.KindTransition, Payload = """{"from":"Act","to":"AwaitingApproval"}""" };

        await _factory.WithDbAsync(db =>
        {
            db.AgentRuns.Add(run);
            db.AgentSteps.Add(step);
            db.AgentPlans.Add(plan);
            db.AgentApprovals.Add(approval);
            db.AgentTraces.Add(trace);
            return Task.CompletedTask;
        });
        return (run, step, approval);
    }

    private static (string Token, string Cookie) ExtractAntiforgery(HttpResponseMessage response, string html)
    {
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        token.Should().NotBeEmpty("the approve/deny forms carry an antiforgery token");
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? string.Join("; ", cookies.Select(c => c.Split(';')[0]))
            : string.Empty;
        return (token, cookie);
    }

    [Fact]
    public async Task Approvals_page_lists_pending_calls_with_tool_arguments_and_reason()
    {
        var (run, _, approval) = await SeedParkedRunAsync("learner-list");
        var client = _factory.CreateClient().AsInstructor("instructor-1");

        var response = await client.GetAsync("/approvals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain($"id=\"approval-{approval.Id}\"");
        html.Should().Contain("data-approval-status=\"Pending\"");
        html.Should().Contain("<code>delete_notes</code>");
        html.Should().Contain("&quot;note_id&quot;: 42");
        html.Should().Contain("always requires approval");
        html.Should().Contain($"href=\"/runs/{run.Id}\"");
        html.Should().Contain("hx-post=\"/approvals?handler=Approve\"").And.Contain("hx-post=\"/approvals?handler=Deny\"");
    }

    [Fact]
    public async Task Approving_through_the_page_records_the_decision_and_hands_the_run_back_to_a_worker()
    {
        var (run, step, approval) = await SeedParkedRunAsync("learner-approve");
        var client = _factory.CreateClient(new() { HandleCookies = true }).AsInstructor("instructor-1");
        var page = await client.GetAsync("/approvals");
        var (token, _) = ExtractAntiforgery(page, await page.Content.ReadAsStringAsync());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/approvals?handler=Approve")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["id"] = approval.Id.ToString(),
                ["note"] = "Fine by me.",
            }),
        };
        request.Headers.Add("HX-Request", "true");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await response.Content.ReadAsStringAsync();
        row.Should().Contain("data-approval-status=\"Approved\"");
        row.Should().Contain("by instructor-1");
        row.Should().Contain("Fine by me.");
        row.Should().NotContain("hx-post", "a decided row has no buttons");

        await _factory.WithDbAsync(async db =>
        {
            var decided = await db.AgentApprovals.SingleAsync(a => a.Id == approval.Id);
            decided.Status.Should().Be(AgentApproval.StatusApproved);
            decided.DecidedBy.Should().Be("instructor-1");
            decided.DecisionNote.Should().Be("Fine by me.");

            var resumed = await db.AgentRuns.SingleAsync(r => r.Id == run.Id);
            resumed.Stage.Should().Be(AgentStage.AwaitingApproval);
            resumed.Status.Should().Be(AgentRun.StatusRunning, "the Web worker's sweep picks up Running runs at AwaitingApproval");

            (await db.AIEscalations.SingleAsync(e => e.Id == decided.EscalationId)).Status.Should().Be("Resolved");
            (await db.AgentTraces.CountAsync(t => t.RunId == run.Id && t.Kind == AgentTrace.KindApproval && t.StepId == step.Id)).Should().Be(1);
        });
    }

    [Fact]
    public async Task Denying_through_the_page_marks_the_approval_denied()
    {
        var (run, _, approval) = await SeedParkedRunAsync("learner-deny");
        var client = _factory.CreateClient(new() { HandleCookies = true }).AsInstructor("instructor-1");
        var page = await client.GetAsync("/approvals");
        var (token, _) = ExtractAntiforgery(page, await page.Content.ReadAsStringAsync());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/approvals?handler=Deny")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["id"] = approval.Id.ToString(),
            }),
        };
        request.Headers.Add("HX-Request", "true");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("data-approval-status=\"Denied\"");
        await _factory.WithDbAsync(async db =>
        {
            (await db.AgentApprovals.SingleAsync(a => a.Id == approval.Id)).Status.Should().Be(AgentApproval.StatusDenied);
            (await db.AgentRuns.SingleAsync(r => r.Id == run.Id)).Status.Should().Be(AgentRun.StatusRunning);
        });

        // A second decision on the same approval is refused.
        using var again = new HttpRequestMessage(HttpMethod.Post, "/approvals?handler=Approve")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["id"] = approval.Id.ToString() }),
        };
        again.Headers.Add("HX-Request", "true");
        (await client.SendAsync(again)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Run_page_shows_the_run_its_steps_and_its_traces_in_order()
    {
        var (run, step, approval) = await SeedParkedRunAsync("learner-trace");
        var client = _factory.CreateClient().AsInstructor("instructor-1");

        var html = await client.GetStringAsync($"/runs/{run.Id}");

        html.Should().Contain($"Agent run <code>{run.Id}</code>");
        html.Should().Contain("data-stage=\"AwaitingApproval\"");
        html.Should().Contain("data-status=\"Waiting\"");
        html.Should().Contain("roles: Learner; courses: 7");
        html.Should().Contain("Revision 0");
        html.Should().Contain("Remove the stale note.");
        html.Should().Contain("data-step-status=\"AwaitingApproval\"");
        html.Should().Contain("data-trace-kind=\"Transition\"");
        html.Should().Contain("data-approval-status=\"Pending\"");
        html.Should().Contain("toolu_del");
        _ = step; _ = approval;

        (await client.GetAsync($"/runs/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var list = await client.GetStringAsync("/runs");
        list.Should().Contain($"href=\"/runs/{run.Id}\"");
    }
}
