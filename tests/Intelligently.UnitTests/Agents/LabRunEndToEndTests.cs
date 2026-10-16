using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tasks;
using Intelligently.Core.Agents.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// The pilot task end to end through the real runner: the five template phases, the reproduce and
/// ground-truth gates, and the learner-facing output (explanation and fix diff) stored on the run.
/// </summary>
public class LabRunEndToEndTests
{
    private const string Explanation =
        "Your program asked for Newtonsoft.Json, a library that is not part of .NET and was not added to this lab, so the compiler could not find it (that is what CS0246 means). " +
        "The fix uses System.Text.Json, which ships with .NET, to print the same JSON.";

    private static DecisionResult Text(string text) => new([], text, "end_turn", EngineStubs.Usage, "{}");
    private static DecisionResult Call(ToolCall call, string text) => new([call], text, "tool_use", EngineStubs.Usage, "{}");

    [Fact]
    public async Task A_graded_lab_runs_through_all_five_phases_and_stores_the_explanation_and_the_fix()
    {
        // The broken program fails to compile; the fixed one prints the expected JSON.
        var sandbox = new FakeSandbox(r => r.Files.Any(f => f.Content.Contains("Newtonsoft")) ? LabSpecs.CompileFailure() : LabSpecs.Success(LabSpecs.ExpectedOutput + "\n"));
        using var host = new AgentTestHost(s =>
        {
            s.RemoveAll<ISandbox>();
            s.AddSingleton<ISandbox>(sandbox);
        });
        EngineStubs.PlanReturns(host.Engine, LabSpecs.TemplatePlan());
        var turns = new Queue<DecisionResult>(
        [
            Call(LabSpecs.RunCall("toolu_repro", LabSpecs.BrokenProgram, LabSpecs.ExpectedOutput), "Reproducing the build."),
            Text("Root cause: line 2 imports Newtonsoft.Json, which is not referenced by the lab; CS0246 is the compiler saying it cannot find that namespace."),
            Text("Fix: replace `using Newtonsoft.Json;` with `using System.Text.Json;` and `JsonConvert.SerializeObject` with `JsonSerializer.Serialize`."),
            Call(LabSpecs.RunCall("toolu_rerun", LabSpecs.FixedProgram, LabSpecs.ExpectedOutput), "Re-running with the fix."),
            Text(Explanation),
        ]);
        host.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => turns.Dequeue());

        var run = await host.CreateRunAsync(taskSpec: LabSpecs.Json(expectedOutput: LabSpecs.ExpectedOutput));
        var (final, stages) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusCompleted);
        stages.Should().NotContain(AgentStage.Recover);
        stages.Count(s => s == AgentStage.Act).Should().Be(5);
        final.Budget.ToolCallsLeft.Should().Be(38);
        final.StatusReason.Should().Contain("Explanation and fix diff stored");
        host.Engine.Verify(e => e.JudgeAsync(It.IsAny<PromptEnvelope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, "ground truth decides a graded lab");

        var steps = await host.QueryAsync(db => db.AgentSteps.Where(s => s.RunId == run.Id).OrderBy(s => s.PlanStepIndex).ToListAsync());
        steps.Should().HaveCount(5).And.OnlyContain(s => s.Status == AgentStep.StatusPassed);
        steps[0].VerifierMessage.Should().StartWith("Reproduced the reported error (exit code 1)").And.Contain("CS0246");
        steps[1].VerifierMessage.Should().Be("Phase 'diagnose' recorded its reasoning.");
        steps[2].VerifierMessage.Should().Be("Phase 'propose_fix' recorded its reasoning.");
        steps[3].VerifierMessage.Should().Be("Ground truth: the fixed lab's stdout matches the expected output.");
        steps[4].VerifierMessage.Should().Contain("ground-truth gate on the re-run already established correctness");

        final.FinalExplanation.Should().Be(Explanation);
        final.FixDiff.Should().Be(
            "--- a/Program.cs\n+++ b/Program.cs\n@@ -1,4 +1,4 @@\n using System;\n-using Newtonsoft.Json;\n+using System.Text.Json;\n \n-Console.WriteLine(JsonConvert.SerializeObject(new { ok = true }));\n+Console.WriteLine(JsonSerializer.Serialize(new { ok = true }));");

        sandbox.Requests.Should().HaveCount(2);
        sandbox.Requests[0].Files.Single().Content.Should().Contain("Newtonsoft");
        sandbox.Requests[1].Files.Single().Content.Should().Contain("System.Text.Json");

        var sandboxLogs = await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id && t.Kind == AgentTrace.KindSandboxLog).ToListAsync());
        sandboxLogs.Should().HaveCount(2);
        sandboxLogs[0].Payload.Should().Contain("\"exitCode\":1");
        sandboxLogs[1].Payload.Should().Contain("\"exitCode\":0");

        var episode = await host.QueryAsync(db => db.AgentEpisodes.SingleAsync(e => e.RunId == run.Id));
        episode.ToolUsageJson.Should().Be("""{"run_lab_code":2}""");
    }

    [Fact]
    public async Task A_reproduce_step_that_does_not_reproduce_is_retried_and_the_explanation_falls_back_to_the_last_text()
    {
        var calls = 0;
        var sandbox = new FakeSandbox(_ => ++calls == 1 ? LabSpecs.Success("clean\n") : LabSpecs.CompileFailure());
        using var host = new AgentTestHost(s =>
        {
            s.RemoveAll<ISandbox>();
            s.AddSingleton<ISandbox>(sandbox);
        });
        EngineStubs.PlanReturns(host.Engine, LabSpecs.ShortPlan(LabPhases.Reproduce));
        var turns = new Queue<DecisionResult>(
        [
            Call(LabSpecs.RunCall("toolu_1", LabSpecs.FixedProgram), "Running what I think is the lab."),
            Call(LabSpecs.RunCall("toolu_2", LabSpecs.BrokenProgram), "Running the submitted files unchanged."),
            Text("The build fails because Newtonsoft.Json is not referenced."),
        ]);
        host.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => turns.Dequeue());
        host.Engine.Setup(e => e.JudgeAsync(It.IsAny<PromptEnvelope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JudgementResult(true, 0.8, "Correct and short.", EngineStubs.Usage, "{}"));

        var run = await host.CreateRunAsync();
        var (final, stages) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusCompleted);
        final.Budget.RetriesLeft.Should().Be(1);
        stages.Should().ContainInOrder(AgentStage.Verify, AgentStage.Recover, AgentStage.Act);
        var first = await host.QueryAsync(db => db.AgentSteps.SingleAsync(s => s.RunId == run.Id && s.PlanStepIndex == 0 && s.Attempt == 1));
        first.Status.Should().Be(AgentStep.StatusFailed);
        first.VerifierMessage.Should().Contain("did not reproduce");
        final.FinalExplanation.Should().Be("The build fails because Newtonsoft.Json is not referenced.");
        final.FixDiff.Should().BeNull("no step after the reproduce phase ran a successful fix");
        final.StatusReason.Should().Contain("no file changes");
    }
}
