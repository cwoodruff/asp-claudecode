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
/// The pilot task end to end in the Web host: a LabErrorDiagnosis run posted to <c>/agent/runs</c> goes
/// through the five template phases on the worker with the fake sandbox and the mocked engine, completes,
/// and the htmx status partial shows the learner the explanation and the fix as a diff.
/// </summary>
public class LabErrorDiagnosisEndpointTests : IClassFixture<AgentWebFactory>
{
    private const string ExpectedOutput = "{\"ok\":true}";
    private const string Explanation = "CS0246 means the compiler could not find a type or namespace: Newtonsoft.Json is a separate package this lab does not include. The fix uses System.Text.Json, which is part of .NET.";

    private static readonly ModelUsage Usage = new("claude-opus-5-5", 100, 20);
    private readonly AgentWebFactory _factory;

    public LabErrorDiagnosisEndpointTests(AgentWebFactory factory)
    {
        _factory = factory;
    }

    private static ToolCall RunCall(string id, string program) => new(id, RunLabCodeTool.ToolName, JsonSerializer.SerializeToElement(new
    {
        language = "csharp",
        files = new[] { new { path = "Program.cs", content = program } },
        entrypoint = "Program.cs",
        expected_output = ExpectedOutput,
    }));

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
    public async Task A_lab_run_completes_through_the_template_and_the_status_partial_shows_the_explanation_and_the_diff()
    {
        _factory.Engine.Reset();
        _factory.Engine.Setup(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanResult(new AgentPlan
            {
                Goal = "Diagnose and fix the CS0246 build error.",
                Steps =
                [
                    new PlanStep { Index = 0, Phase = "reproduce", Description = "Run the submitted files.", Tool = RunLabCodeTool.ToolName, SuccessCriteria = "The build fails with CS0246." },
                    new PlanStep { Index = 1, Phase = "diagnose", Description = "Name the cause.", Tool = null, SuccessCriteria = "A root cause." },
                    new PlanStep { Index = 2, Phase = "propose_fix", Description = "Propose the change.", Tool = null, SuccessCriteria = "The changed lines." },
                    new PlanStep { Index = 3, Phase = "rerun", Description = "Run the fixed files.", Tool = RunLabCodeTool.ToolName, SuccessCriteria = "Exit code 0 and the expected output." },
                    new PlanStep { Index = 4, Phase = "explain", Description = "Explain to a beginner.", Tool = null, SuccessCriteria = "A beginner-level explanation." },
                ],
            }, Usage, "{}"));
        var turns = new Queue<DecisionResult>(
        [
            new([RunCall("toolu_repro", TestSpecs.BrokenProgram)], "Reproducing.", "tool_use", Usage, "{}"),
            new([], "Line 2 imports Newtonsoft.Json, which is not referenced.", "end_turn", Usage, "{}"),
            new([], "Replace Newtonsoft.Json with System.Text.Json.", "end_turn", Usage, "{}"),
            new([RunCall("toolu_rerun", TestSpecs.FixedProgram)], "Re-running.", "tool_use", Usage, "{}"),
            new([], Explanation, "end_turn", Usage, "{}"),
        ]);
        _factory.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { lock (turns) return turns.Dequeue(); });
        _factory.Sandbox.Script = request => request.Files.Any(f => f.Content.Contains("Newtonsoft"))
            ? new SandboxResult("fake", 1, string.Empty, "Program.cs(2,7): " + TestSpecs.ErrorText + "\n", false, false, TimeSpan.FromMilliseconds(400))
            : new SandboxResult("fake", 0, ExpectedOutput + "\n", string.Empty, false, false, TimeSpan.FromMilliseconds(300));
        var client = _factory.CreateClient().AsLearner("learner-lab-pilot", 7);

        try
        {
            var created = await client.PostAsJsonAsync("/agent/runs", new { courseId = 7, taskType = "LabErrorDiagnosis", taskSpec = TestSpecs.LabSpec(courseId: 7, expectedOutput: ExpectedOutput) });
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

            var html = await PollUntilAsync(client, id, AgentRun.StatusCompleted, TimeSpan.FromSeconds(30));

            html.Should().Contain("data-stage=\"Completed\"");
            html.Should().Contain("data-explanation").And.Contain("CS0246 means the compiler could not find a type or namespace");
            html.Should().Contain("data-fix-diff").And.Contain("-using Newtonsoft.Json;").And.Contain("+using System.Text.Json;");
            html.Should().NotContain("hx-trigger", "a completed run stops polling");

            await _factory.WithDbAsync(async db =>
            {
                var run = await db.AgentRuns.SingleAsync(r => r.Id == id);
                run.FinalExplanation.Should().Be(Explanation);
                run.FixDiff.Should().StartWith("--- a/Program.cs");
                var steps = await db.AgentSteps.Where(s => s.RunId == id).OrderBy(s => s.PlanStepIndex).ToListAsync();
                steps.Should().HaveCount(5).And.OnlyContain(s => s.Status == AgentStep.StatusPassed);
                steps[0].VerifierMessage.Should().StartWith("Reproduced the reported error");
                steps[3].VerifierMessage.Should().StartWith("Ground truth");
                var transitions = await db.AgentTraces.Where(t => t.RunId == id && t.Kind == AgentTrace.KindTransition).OrderBy(t => t.Id).Select(t => t.Stage).ToListAsync();
                transitions.Should().StartWith([AgentStage.Interpret, AgentStage.Plan]).And.EndWith(AgentStage.Learn);
                transitions.Should().NotContain(AgentStage.Recover);
                (await db.AgentTraces.CountAsync(t => t.RunId == id && t.Kind == AgentTrace.KindSandboxLog)).Should().Be(2);
                (await db.AgentTraces.CountAsync(t => t.RunId == id && t.Kind == AgentTrace.KindJudgement)).Should().Be(0, "ground truth decided");
            });
            _factory.Engine.Verify(e => e.JudgeAsync(It.IsAny<PromptEnvelope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            _factory.Sandbox.Script = null;
        }
    }

    [Fact]
    public async Task A_malformed_lab_spec_is_accepted_by_the_endpoint_but_fails_at_Interpret_with_the_reason()
    {
        _factory.Engine.Reset();
        var client = _factory.CreateClient().AsLearner("learner-lab-bad", 7);

        var created = await client.PostAsJsonAsync("/agent/runs", new { courseId = 7, taskType = "LabErrorDiagnosis", taskSpec = new { question = "My build fails with CS0246." } });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var html = await PollUntilAsync(client, id, AgentRun.StatusFailed, TimeSpan.FromSeconds(30));

        html.Should().Contain("data-stage=\"Failed\"");
        html.Should().Contain("Task spec for LabErrorDiagnosis is invalid").And.Contain("required");
        html.Should().NotContain("data-explanation");
        _factory.Engine.VerifyNoOtherCalls();
    }
}
