using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Intelligently.IntegrationTests.Agents;

/// <summary>
/// No agent route in the Web host works without an authenticated identity, the principal a run acts
/// under comes from the caller's claims only, and a run is visible to its owner and to instructors.
/// </summary>
public class AgentAuthorizationTests : IClassFixture<AgentWebFactory>
{
    private static readonly ModelUsage Usage = new("claude-opus-5-5", 100, 20);
    private readonly AgentWebFactory _factory;

    public AgentAuthorizationTests(AgentWebFactory factory)
    {
        _factory = factory;
        StubHappyPath();
    }

    private void StubHappyPath()
    {
        _factory.Engine.Reset();
        _factory.Engine.Setup(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanResult(new AgentPlan
            {
                Goal = "Explain CS0246.",
                Steps = [new PlanStep { Index = 0, Description = "Look up CS0246.", Tool = "lookup_knowledge", SuccessCriteria = "An entry is found." }],
            }, Usage, "{}"));
        _factory.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                [new ToolCall("toolu_01", "lookup_knowledge", JsonSerializer.SerializeToElement(new { query = "CS0246", course_id = (int?)null }))],
                "Looking it up.", "tool_use", Usage, "{}"));
    }

    private static object Body(int courseId = 7) => new { courseId, taskType = "LabErrorDiagnosis", taskSpec = new { question = "q" } };

    private static async Task<Guid> CreateRunAsync(HttpClient client, int courseId = 7)
    {
        var created = await client.PostAsJsonAsync("/agent/runs", Body(courseId));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static HttpRequestMessage HtmxGet(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("HX-Request", "true");
        return request;
    }

    [Fact]
    public async Task Anonymous_JSON_post_to_create_a_run_gets_401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/agent/runs", Body());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await _factory.WithDbAsync(async db => (await db.AgentRuns.CountAsync(r => r.TaskType == "LabErrorDiagnosis" && r.UserId == "")).Should().Be(0));
    }

    [Fact]
    public async Task Anonymous_browser_requests_are_redirected_to_the_login_page()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var form = await client.GetAsync("/agent");
        form.StatusCode.Should().Be(HttpStatusCode.Redirect);
        form.RedirectPath().Should().StartWith("/Account/Login").And.Contain("ReturnUrl=%2Fagent");

        var run = await client.GetAsync($"/agent/runs/{Guid.NewGuid()}");
        run.StatusCode.Should().Be(HttpStatusCode.Redirect);
        run.RedirectPath().Should().StartWith("/Account/Login");

        (await client.GetAsync("/Account/Login")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Anonymous_htmx_poll_of_a_run_gets_401_not_a_login_page()
    {
        var owner = _factory.CreateClient().AsLearner("learner-auth-owner", 7);
        var id = await CreateRunAsync(owner);
        var anonymous = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await anonymous.SendAsync(HtmxGet($"/agent/runs/{id}"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_userId_in_the_body_is_ignored_and_the_run_acts_as_the_caller()
    {
        var client = _factory.CreateClient().AsLearner("learner-real", 7);

        var created = await client.PostAsJsonAsync("/agent/runs", new
        {
            userId = "someone-else",
            courseId = 7,
            taskType = "LabErrorDiagnosis",
            taskSpec = new { question = "q" },
        });

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await _factory.WithDbAsync(async db =>
        {
            var run = await db.AgentRuns.SingleAsync(r => r.Id == id);
            run.UserId.Should().Be("learner-real");
            run.Principal.UserId.Should().Be("learner-real");
            run.Principal.Roles.Should().Equal(RunPrincipal.LearnerRole);
            run.Principal.CourseScope.Should().Equal(7);
        });
    }

    [Fact]
    public async Task Creating_a_run_in_a_course_the_caller_is_not_enrolled_in_gets_403()
    {
        var client = _factory.CreateClient().AsLearner("learner-elsewhere", 3, 4);

        var response = await client.PostAsJsonAsync("/agent/runs", Body(courseId: 7));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _factory.WithDbAsync(async db => (await db.AgentRuns.CountAsync(r => r.UserId == "learner-elsewhere")).Should().Be(0));
    }

    [Fact]
    public async Task The_principal_carries_every_role_and_course_from_the_claims()
    {
        var client = _factory.CreateClient().As("ta-1", [RunPrincipal.LearnerRole, RunPrincipal.InstructorRole], 7, 9);

        var id = await CreateRunAsync(client, courseId: 9);

        await _factory.WithDbAsync(async db =>
        {
            var run = await db.AgentRuns.SingleAsync(r => r.Id == id);
            run.CourseId.Should().Be(9);
            run.Principal.Roles.Should().BeEquivalentTo([RunPrincipal.LearnerRole, RunPrincipal.InstructorRole]);
            run.Principal.CourseScope.Should().Equal(7, 9);
        });
    }

    [Fact]
    public async Task A_run_is_visible_to_its_owner_and_to_instructors_but_not_to_another_learner()
    {
        var owner = _factory.CreateClient().AsLearner("learner-owner", 7);
        var other = _factory.CreateClient().AsLearner("learner-other", 7);
        var instructor = _factory.CreateClient().AsInstructor("instructor-1");
        var id = await CreateRunAsync(owner);

        (await owner.GetAsync($"/agent/runs/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.SendAsync(HtmxGet($"/agent/runs/{id}"))).StatusCode.Should().Be(HttpStatusCode.OK);

        var instructorPage = await instructor.GetAsync($"/agent/runs/{id}");
        instructorPage.StatusCode.Should().Be(HttpStatusCode.OK);
        (await instructorPage.Content.ReadAsStringAsync()).Should().Contain($"id=\"run-{id}\"");

        (await other.GetAsync($"/agent/runs/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.SendAsync(HtmxGet($"/agent/runs/{id}"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_demo_form_shows_the_caller_and_has_no_user_id_field()
    {
        var client = _factory.CreateClient().AsLearner("learner-form", 7);

        var html = await client.GetStringAsync("/agent");

        html.Should().Contain("Running as <strong>learner-form</strong>");
        html.Should().Contain("enrolled in course(s) 7");
        html.Should().NotContain("name=\"UserId\"");
        html.Should().Contain("name=\"CourseId\"");
    }
}
