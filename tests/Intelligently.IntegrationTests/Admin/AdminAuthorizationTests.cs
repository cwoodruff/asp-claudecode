using System.Net;
using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.IntegrationTests.Admin;

/// <summary>The Admin agent pages require the Instructor policy: anonymous callers are sent to sign in, learners are refused.</summary>
public class AdminAuthorizationTests : IClassFixture<AdminWebFactory>
{
    private readonly AdminWebFactory _factory;

    public AdminAuthorizationTests(AdminWebFactory factory)
    {
        _factory = factory;
    }

    private async Task<AgentApproval> SeedPendingApprovalAsync(string userId)
    {
        var run = new AgentRun
        {
            UserId = userId, CourseId = 7, TaskType = "NoteCleanup", TaskSpec = """{"noteId":42}""",
            Stage = AgentStage.AwaitingApproval, Status = AgentRun.StatusWaiting, Principal = RunPrincipal.Learner(userId, 7),
        };
        var step = new AgentStep
        {
            RunId = run.Id, PlanRevision = 0, PlanStepIndex = 0, Attempt = 1, Status = AgentStep.StatusAwaitingApproval,
            ToolCallsJson = JsonSerializer.Serialize(new[] { new ToolCall("toolu_del", "delete_notes", JsonSerializer.SerializeToElement(new { course_id = 7, note_id = 42 })) }),
            ToolResultsJson = "[]",
        };
        var approval = new AgentApproval
        {
            RunId = run.Id, StepId = step.Id, ToolName = "delete_notes",
            ToolCallJson = JsonSerializer.Serialize(new ToolCall("toolu_del", "delete_notes", JsonSerializer.SerializeToElement(new { course_id = 7, note_id = 42 }))),
            ArgsHash = new string('A', 64), Reason = "Destructive tool 'delete_notes' always requires approval.",
        };
        await _factory.WithDbAsync(db =>
        {
            db.AgentRuns.Add(run);
            db.AgentSteps.Add(step);
            db.AgentApprovals.Add(approval);
            return Task.CompletedTask;
        });
        return approval;
    }

    private static HttpRequestMessage ApprovePost(Guid approvalId, bool htmx)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/approvals?handler=Approve")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = approvalId.ToString() }),
        };
        if (htmx) request.Headers.Add("HX-Request", "true");
        return request;
    }

    [Theory]
    [InlineData("/approvals")]
    [InlineData("/runs")]
    public async Task Anonymous_browser_requests_are_redirected_to_the_login_page(string path)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.RedirectPath().Should().StartWith("/Account/Login").And.Contain("ReturnUrl=");
        (await client.GetAsync("/Account/Login")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Anonymous_request_for_a_run_page_is_redirected_to_the_login_page()
    {
        var approval = await SeedPendingApprovalAsync("learner-anon-run");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/runs/{approval.RunId}");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.RedirectPath().Should().StartWith("/Account/Login");
    }

    [Fact]
    public async Task Anonymous_htmx_approve_gets_401_and_decides_nothing()
    {
        var approval = await SeedPendingApprovalAsync("learner-anon-approve");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.SendAsync(ApprovePost(approval.Id, htmx: true));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await _factory.WithDbAsync(async db => (await db.AgentApprovals.SingleAsync(a => a.Id == approval.Id)).Status.Should().Be(AgentApproval.StatusPending));
    }

    [Fact]
    public async Task A_learner_cannot_see_the_queue_or_the_runs()
    {
        var approval = await SeedPendingApprovalAsync("learner-self");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false }).AsLearner("learner-self", 7);

        var queue = await client.GetAsync("/approvals");
        queue.StatusCode.Should().Be(HttpStatusCode.Redirect);
        queue.RedirectPath().Should().StartWith("/Account/AccessDenied");

        (await client.GetAsync("/runs")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await client.GetAsync($"/runs/{approval.RunId}")).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task A_learner_cannot_approve_even_their_own_run()
    {
        var approval = await SeedPendingApprovalAsync("learner-approver");
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false }).AsLearner("learner-approver", 7);

        var htmx = await client.SendAsync(ApprovePost(approval.Id, htmx: true));
        htmx.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var browser = await client.SendAsync(ApprovePost(approval.Id, htmx: false));
        browser.StatusCode.Should().Be(HttpStatusCode.Redirect);
        browser.RedirectPath().Should().StartWith("/Account/AccessDenied");

        await _factory.WithDbAsync(async db =>
        {
            var pending = await db.AgentApprovals.SingleAsync(a => a.Id == approval.Id);
            pending.Status.Should().Be(AgentApproval.StatusPending);
            pending.DecidedBy.Should().BeNull();
            (await db.AgentRuns.SingleAsync(r => r.Id == approval.RunId)).Status.Should().Be(AgentRun.StatusWaiting);
        });
    }

    [Fact]
    public async Task An_instructor_sees_the_queue_and_the_run_page()
    {
        var approval = await SeedPendingApprovalAsync("learner-visible");
        var client = _factory.CreateClient().AsInstructor("instructor-2");

        var queue = await client.GetAsync("/approvals");
        queue.StatusCode.Should().Be(HttpStatusCode.OK);
        (await queue.Content.ReadAsStringAsync()).Should().Contain($"id=\"approval-{approval.Id}\"").And.Contain("Signed in as instructor-2");

        (await client.GetAsync($"/runs/{approval.RunId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/runs")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
