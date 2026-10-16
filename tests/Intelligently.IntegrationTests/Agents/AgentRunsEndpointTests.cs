using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Entities.AI;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Intelligently.IntegrationTests.Agents;

/// <summary>A run can be created and observed through the Web endpoints while the worker drives it.</summary>
public class AgentRunsEndpointTests : IClassFixture<AgentWebFactory>
{
    private static readonly ModelUsage Usage = new("claude-opus-5-5", 100, 20);
    private readonly AgentWebFactory _factory;

    public AgentRunsEndpointTests(AgentWebFactory factory)
    {
        _factory = factory;
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

    private static async Task<string> PollUntilSettledAsync(HttpClient client, Guid id, TimeSpan timeout)
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
            if (!Regex.IsMatch(html, "data-status=\"Running\"")) return html;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);

        return html;
    }

    [Fact]
    public async Task Post_creates_a_run_and_Get_partial_shows_it_progress_to_Completed()
    {
        StubHappyPath();
        await _factory.WithDbAsync(db =>
        {
            db.AIKnowledgeEntries.Add(new AIKnowledgeEntry
            {
                CourseId = 7, EntryType = "LabError", Question = "CS0246?",
                Answer = "CS0246 means a type or namespace was not found.", AuthoredByUserId = "instructor-1",
            });
            return Task.CompletedTask;
        });
        var client = _factory.CreateClient().AsLearner("learner-1", 7);

        var created = await client.PostAsJsonAsync("/agent/runs", new
        {
            courseId = 7,
            taskType = "LabErrorDiagnosis",
            taskSpec = TestSpecs.LabSpec(courseId: 7),
        });

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        body.GetProperty("stage").GetString().Should().Be("Interpret");
        body.GetProperty("status").GetString().Should().Be("Running");
        created.Headers.Location!.ToString().Should().Be($"/agent/runs/{id}");

        var html = await PollUntilSettledAsync(client, id, TimeSpan.FromSeconds(30));

        html.Should().Contain($"id=\"run-{id}\"");
        html.Should().Contain("data-stage=\"Completed\"");
        html.Should().Contain("data-status=\"Completed\"");
        html.Should().NotContain("hx-trigger", "a finished run stops polling");

        await _factory.WithDbAsync(async db =>
        {
            var run = await db.AgentRuns.SingleAsync(r => r.Id == id);
            run.Status.Should().Be(AgentRun.StatusCompleted);
            (await db.AgentEpisodes.CountAsync(e => e.RunId == id)).Should().Be(1);
            (await db.AgentTraces.CountAsync(t => t.RunId == id && t.Kind == AgentTrace.KindToolCall)).Should().Be(1);
        });
    }

    [Fact]
    public async Task Get_without_HX_Request_returns_the_full_page_with_the_partial()
    {
        StubHappyPath();
        var client = _factory.CreateClient().AsLearner("learner-2", 7);
        var created = await client.PostAsJsonAsync("/agent/runs", new
        {
            courseId = 7, taskType = "LabErrorDiagnosis", taskSpec = TestSpecs.LabSpec(courseId: 7),
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var page = await client.GetStringAsync($"/agent/runs/{id}");

        page.Should().Contain("<h1>Agent run</h1>");
        page.Should().Contain($"id=\"run-{id}\"");
        page.Should().Contain("data-stage=");
    }

    [Fact]
    public async Task Get_unknown_run_returns_404()
    {
        var client = _factory.CreateClient().AsLearner("learner-404", 7);
        var response = await client.GetAsync($"/agent/runs/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_without_task_type_returns_400()
    {
        var client = _factory.CreateClient().AsLearner("learner-3", 7);
        var response = await client.PostAsJsonAsync("/agent/runs", new
        {
            courseId = 7, taskType = "", taskSpec = new { question = "q" },
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
