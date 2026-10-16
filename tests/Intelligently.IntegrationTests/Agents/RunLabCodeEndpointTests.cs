using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Execution;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Intelligently.IntegrationTests.Agents;

/// <summary>
/// A run in the Web host that calls <c>run_lab_code</c> end to end: the worker dispatches it through the
/// Control Plane to the fake sandbox, the output is schema-valid, and the run completes.
/// </summary>
public class RunLabCodeEndpointTests : IClassFixture<AgentWebFactory>
{
    private static readonly ModelUsage Usage = new("claude-opus-5-5", 100, 20);
    private readonly AgentWebFactory _factory;

    public RunLabCodeEndpointTests(AgentWebFactory factory)
    {
        _factory = factory;
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

    [Fact]
    public async Task Run_calls_run_lab_code_through_the_fake_sandbox_and_completes()
    {
        _factory.Engine.Reset();
        _factory.Engine.Setup(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanResult(new AgentPlan
            {
                Goal = "Reproduce the learner's build.",
                Steps = [new PlanStep { Index = 0, Description = "Run the lab.", Tool = RunLabCodeTool.ToolName, SuccessCriteria = "The program prints hi." }],
            }, Usage, "{}"));
        _factory.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                [new ToolCall("toolu_lab", RunLabCodeTool.ToolName, JsonSerializer.SerializeToElement(new
                {
                    language = "csharp",
                    files = new[] { new { path = "Program.cs", content = "System.Console.WriteLine(\"hi\");" } },
                    entrypoint = "Program.cs",
                    expected_output = "hi",
                }))],
                "Running the lab.", "tool_use", Usage, "{}"));
        var client = _factory.CreateClient();
        var before = _factory.Sandbox.Requests.Count;

        var created = await client.PostAsJsonAsync("/agent/runs", new { userId = "learner-lab", courseId = 7, taskType = "LabErrorDiagnosis", taskSpec = new { error = "none" } });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var completed = await PollUntilAsync(client, id, AgentRun.StatusCompleted, TimeSpan.FromSeconds(30));
        completed.Should().Contain("data-stage=\"Completed\"");

        _factory.Sandbox.Requests.Should().HaveCount(before + 1);
        var request = _factory.Sandbox.Requests[^1];
        request.Command.Should().Equal("dotnet", "run", "Program.cs");
        request.Files.Should().ContainSingle().Which.Content.Should().Contain("WriteLine");

        await _factory.WithDbAsync(async db =>
        {
            var step = await db.AgentSteps.SingleAsync(s => s.RunId == id);
            step.Status.Should().Be(AgentStep.StatusPassed);
            var results = JsonSerializer.Deserialize<List<ToolResult>>(step.ToolResultsJson!)!;
            var output = JsonSerializer.Deserialize<RunLabCodeOutput>(results.Single().Content)!;
            output.Succeeded.Should().BeTrue();
            output.Stdout.Should().Be("hi\n");
            output.MatchesExpectedOutput.Should().BeTrue();
            output.Sandbox.Should().Be("fake");
            var transitions = await db.AgentTraces.Where(t => t.RunId == id && t.Kind == AgentTrace.KindTransition).OrderBy(t => t.Id).Select(t => t.Stage).ToListAsync();
            transitions.Should().Equal(AgentStage.Interpret, AgentStage.Plan, AgentStage.Act, AgentStage.Observe, AgentStage.Verify, AgentStage.Learn);
        });
    }
}
