using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tasks;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Execution;
using Intelligently.Infrastructure.Agents.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// Verify for the lab task (plan section 2.4): deterministic assertions on the sandbox result run first,
/// a re-run passes the ground-truth gate when the spec has an expected output, and otherwise the explanation
/// is judged by the model with the score and rationale stored as evidence.
/// </summary>
public class LabVerifyTests
{
    private static readonly LabErrorDiagnosisContract Contract = new();

    private static AgentTestHost Host(FakeSandbox sandbox) => new(services =>
    {
        services.RemoveAll<ISandbox>();
        services.AddSingleton<ISandbox>(sandbox);
    });

    /// <summary>DecideAsync answers in order and repeats the last answer (retries re-ask for the same step).</summary>
    private static void DecideSequence(AgentTestHost host, params DecisionResult[] decisions)
    {
        var index = 0;
        host.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => decisions[Math.Min(index++, decisions.Length - 1)]);
    }

    private static DecisionResult Text(string text) => new([], text, "end_turn", EngineStubs.Usage, "{}");
    private static DecisionResult Call(ToolCall call) => new([call], "Running the lab.", "tool_use", EngineStubs.Usage, "{}");

    /// <summary>A verification context for one plan step; the sandbox calls behind the results default to running the submitted files.</summary>
    private static StepVerification Context(AgentRun run, PlanStep planStep, AgentStep step, params ToolResult[] results) =>
        Context(run, planStep, step, results.Select(r => LabSpecs.RunCall(r.ToolCallId, LabSpecs.BrokenProgram)).ToList(), results, []);

    private static StepVerification Context(AgentRun run, PlanStep planStep, AgentStep step, IReadOnlyList<ToolCall> calls, IReadOnlyList<ToolResult> results, IReadOnlyList<AgentStep> prior) =>
        new(run, new AgentPlan { Goal = "g", Steps = [planStep] }, planStep, step, calls, results, prior);

    private static ToolResult SandboxResult(SandboxResult result, bool? matches = null, string id = "toolu_lab")
    {
        var output = new RunLabCodeOutput(result.Sandbox, result.ExitCode, result.Stdout, result.Stderr, result.TimedOut, result.MemoryExceeded, result.OutputTruncated, 0.5, result.Succeeded, matches, result.Error);
        return ToolResult.Ok(new ToolCall(id, RunLabCodeTool.ToolName, JsonSerializer.SerializeToElement(new { })), JsonSerializer.SerializeToElement(output, Intelligently.Infrastructure.Agents.Intelligence.AgentJsonSchema.SerializerOptions));
    }

    /// <summary>The plan with all five template steps, plus an earlier step of it that passed, so an explain step has a proven re-run behind it.</summary>
    private static StepVerification TemplateContext(AgentRun run, int stepIndex, AgentStep step, IReadOnlyList<ToolCall> calls, IReadOnlyList<ToolResult> results, params int[] passedStepIndexes)
    {
        var plan = LabSpecs.TemplatePlan();
        var prior = passedStepIndexes.Select(i => new AgentStep { PlanRevision = plan.Revision, PlanStepIndex = i, Status = AgentStep.StatusPassed }).ToList();
        return new StepVerification(run, plan, plan.Steps[stepIndex], step, calls, results, prior);
    }

    private static AgentRun RunWithSpec(string? expectedOutput = null) => new()
    {
        UserId = "learner-1", CourseId = 7, TaskType = LabErrorDiagnosisSpec.TaskType,
        TaskSpec = LabSpecs.Json(expectedOutput: expectedOutput), Principal = RunPrincipal.Learner("learner-1", 7),
    };

    // ── Deterministic assertions and the ground-truth gate (contract unit) ────────────────────────

    [Fact]
    public void Reproduce_passes_when_the_error_reproduces_and_fails_when_the_code_runs_clean()
    {
        var run = RunWithSpec();
        var planStep = LabSpecs.TemplatePlan().Steps[0];
        var step = new AgentStep { AssistantText = "Running." };

        Contract.VerifyStep(Context(run, planStep, step, SandboxResult(LabSpecs.CompileFailure())))
            .Should().BeEquivalentTo(StepVerdict.Pass("Reproduced the reported error (exit code 1): Program.cs(2,7): " + LabSpecs.ErrorText + "\n\nThe build failed. Fix the build errors and run again."));
        Contract.VerifyStep(Context(run, planStep, step, SandboxResult(LabSpecs.Success("fine\n"))))!
            .Should().Match<StepVerdict>(v => !v.Passed && v.Message.Contains("did not reproduce"));

        // With an expected output, running clean but printing the wrong thing still reproduces the problem.
        Contract.VerifyStep(Context(RunWithSpec("right"), planStep, step, SandboxResult(LabSpecs.Success("wrong\n"))))!
            .Should().Match<StepVerdict>(v => v.Passed && v.Message.Contains("output differs"));

        Contract.VerifyStep(Context(run, planStep, step))!
            .Should().Match<StepVerdict>(v => !v.Passed && v.Message.Contains("no sandbox result"));

        // Reproduce must run the submission as it is: a changed file, an extra file or a missing course file is not a reproduction.
        var failure = SandboxResult(LabSpecs.CompileFailure());
        Contract.VerifyStep(Context(run, planStep, step, [LabSpecs.RunCall(failure.ToolCallId, LabSpecs.FixedProgram)], [failure], []))!
            .Should().Match<StepVerdict>(v => !v.Passed && v.Message.Contains("submitted files unchanged"));
        var withCourseFile = RunWithSpec();
        withCourseFile.TaskSpec = LabSpecs.Json(courseFiles: LabSpecs.WithCourseFile);
        Contract.VerifyStep(Context(withCourseFile, planStep, step, [LabSpecs.RunCall(failure.ToolCallId, LabSpecs.BrokenProgram)], [failure], []))!
            .Should().Match<StepVerdict>(v => !v.Passed && v.Message.Contains("submitted files unchanged"), "the course file was left out");
        Contract.VerifyStep(Context(withCourseFile, planStep, step, [LabSpecs.RunCall(failure.ToolCallId, LabSpecs.BrokenProgram, courseFiles: LabSpecs.WithCourseFile)], [failure], []))!
            .Passed.Should().BeTrue();
    }

    [Fact]
    public void Sandbox_calls_must_run_the_spec_language_and_entrypoint()
    {
        var run = RunWithSpec(LabSpecs.ExpectedOutput);
        var rerun = LabSpecs.TemplatePlan().Steps[3];
        var step = new AgentStep { AssistantText = "Re-running." };
        var success = SandboxResult(LabSpecs.Success(LabSpecs.ExpectedOutput + "\n"));

        Contract.VerifyStep(Context(run, rerun, step, [LabSpecs.RunCall(success.ToolCallId, "print('{\"ok\":true}')", language: "python", entrypoint: "lab.py")], [success], []))!
            .Should().Match<StepVerdict>(v => !v.Passed && v.Message.Contains("'python'") && v.Message.Contains("'csharp'"), "a graded C# lab cannot pass by running a Python file that prints the answer");
        Contract.VerifyStep(Context(run, rerun, step, [LabSpecs.RunCall(success.ToolCallId, LabSpecs.FixedProgram, entrypoint: "Other.cs")], [success], []))!
            .Should().Match<StepVerdict>(v => !v.Passed && v.Message.Contains("entrypoint 'Other.cs'"));
        Contract.VerifyStep(Context(run, rerun, step, [LabSpecs.RunCall(success.ToolCallId, LabSpecs.FixedProgram, entrypoint: "./Program.cs")], [success], []))!
            .Passed.Should().BeTrue("an aliased spelling of the entrypoint is the same file");
        Contract.VerifyStep(Context(run, rerun, step, [new ToolCall("toolu_other", RunLabCodeTool.ToolName, JsonSerializer.SerializeToElement(new { }))], [success], []))!
            .Should().Match<StepVerdict>(v => !v.Passed && v.Message.Contains("cannot be matched"));
    }

    [Fact]
    public void Rerun_applies_exit_code_compiler_output_and_the_ground_truth_gate()
    {
        var rerun = LabSpecs.TemplatePlan().Steps[3];
        var step = new AgentStep { AssistantText = "Re-running." };

        Contract.VerifyStep(Context(RunWithSpec(), rerun, step, SandboxResult(LabSpecs.CompileFailure())))!
            .Should().Match<StepVerdict>(v => !v.Passed && v.Message.Contains("exited with code 1"));
        Contract.VerifyStep(Context(RunWithSpec(), rerun, step, SandboxResult(new SandboxResult("fake", 0, "warning: error CS0168: unused variable\n", string.Empty, false, false, TimeSpan.Zero))))!
            .Should().Match<StepVerdict>(v => !v.Passed && v.Message.Contains("still reports errors"));
        Contract.VerifyStep(Context(RunWithSpec(), rerun, step, SandboxResult(LabSpecs.Success("anything\n"))))!
            .Should().Match<StepVerdict>(v => v.Passed && v.Message.Contains("no expected output"));

        Contract.VerifyStep(Context(RunWithSpec(LabSpecs.ExpectedOutput), rerun, step, SandboxResult(LabSpecs.Success(LabSpecs.ExpectedOutput + "\r\n"))))!
            .Should().Match<StepVerdict>(v => v.Passed && v.Message.StartsWith("Ground truth"));
        Contract.VerifyStep(Context(RunWithSpec(LabSpecs.ExpectedOutput), rerun, step, SandboxResult(LabSpecs.Success("{\"ok\":false}\n"))))!
            .Should().Match<StepVerdict>(v => !v.Passed && v.Message.Contains("differs from the expected output"));

        // Timeouts and memory kills fail before any phase logic, whatever the phase.
        Contract.VerifyStep(Context(RunWithSpec(), LabSpecs.TemplatePlan().Steps[0], step, SandboxResult(new SandboxResult("fake", null, "partial", string.Empty, true, false, TimeSpan.FromSeconds(60)))))!
            .Should().Match<StepVerdict>(v => !v.Passed && v.Message.Contains("timed out"));
        Contract.VerifyStep(Context(RunWithSpec(), rerun, step, SandboxResult(new SandboxResult("fake", 137, string.Empty, "Killed", false, true, TimeSpan.Zero))))!
            .Should().Match<StepVerdict>(v => !v.Passed && v.Message.Contains("memory limit"));
    }

    [Fact]
    public void Explain_is_decided_deterministically_with_an_expected_output_and_judged_without_one()
    {
        var explain = LabSpecs.TemplatePlan().Steps[4];
        var step = new AgentStep { AssistantText = "Newtonsoft is a package you have not referenced; System.Text.Json ships with .NET." };

        var withGroundTruth = TemplateContext(RunWithSpec(LabSpecs.ExpectedOutput), 4, step, [], [], 0, 1, 2, 3);
        Contract.VerifyStep(withGroundTruth)!.Passed.Should().BeTrue();
        Contract.JudgementRubric(withGroundTruth).Should().BeNull();

        // A graded lab whose plan never proved the fix by a passing re-run is not done, whatever the explanation says.
        var unproven = TemplateContext(RunWithSpec(LabSpecs.ExpectedOutput), 4, step, [], [], 0, 1, 2);
        Contract.VerifyStep(unproven)!.Should().Match<StepVerdict>(v => !v.Passed && v.Message.Contains("no re-run of the fix passed"));

        var withoutGroundTruth = Context(RunWithSpec(), explain, step);
        Contract.VerifyStep(withoutGroundTruth).Should().BeNull("the model judges the explanation");
        var judgement = Contract.JudgementRubric(withoutGroundTruth)!;
        judgement.Rubric.Should().Contain("Correctness of the diagnosis").And.Contain("Beginner").And.Contain("CS0246");
        judgement.MinimumScore.Should().Be(LabErrorDiagnosisContract.ExplanationMinimumScore);
        Contract.VerifyStep(Context(new AgentRun { UserId = "l", CourseId = 7, TaskType = LabErrorDiagnosisSpec.TaskType, TaskSpec = LabSpecs.Json(expectedOutput: ""), Principal = RunPrincipal.Learner("l", 7) }, explain, step))!
            .Passed.Should().BeFalse("an empty expected output is a known answer, so the lab is graded and needs a proven re-run");

        Contract.VerifyStep(Context(RunWithSpec(), explain, new AgentStep { AssistantText = " " }))!
            .Should().Match<StepVerdict>(v => !v.Passed && v.Message.Contains("no text"));
        Contract.VerifyStep(Context(RunWithSpec(), LabSpecs.TemplatePlan().Steps[1], step))!.Passed.Should().BeTrue("diagnose only needs text");
        Contract.VerifyStep(Context(RunWithSpec(), new PlanStep { Index = 0, Description = "d", SuccessCriteria = "s" }, step)).Should().BeNull("untagged steps are left to the generic checks");
    }

    // ── Through the runner ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ground_truth_mismatch_fails_the_rerun_step_and_a_matching_rerun_passes()
    {
        var attempt = 0;
        var sandbox = new FakeSandbox(_ => ++attempt == 1 ? LabSpecs.Success("{\"ok\":false}\n") : LabSpecs.Success(LabSpecs.ExpectedOutput + "\n"));
        using var host = Host(sandbox);
        EngineStubs.PlanReturns(host.Engine, LabSpecs.ShortPlan(LabPhases.Rerun));
        DecideSequence(host, Call(LabSpecs.RunCall("toolu_1", LabSpecs.FixedProgram)), Call(LabSpecs.RunCall("toolu_2", LabSpecs.FixedProgram)), Text("Done: the fix removes the missing package."));

        var run = await host.CreateRunAsync(taskSpec: LabSpecs.Json(expectedOutput: LabSpecs.ExpectedOutput));
        var (final, stages) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusCompleted);
        final.Budget.RetriesLeft.Should().Be(1);
        stages.Should().ContainInOrder(AgentStage.Verify, AgentStage.Recover, AgentStage.Act);
        var steps = await host.QueryAsync(db => db.AgentSteps.Where(s => s.RunId == run.Id).OrderBy(s => s.StartedAt).ThenBy(s => s.Attempt).ToListAsync());
        steps.Select(s => s.Status).Should().Equal(AgentStep.StatusFailed, AgentStep.StatusPassed, AgentStep.StatusPassed);
        steps[0].VerifierMessage.Should().Contain("Ground truth").And.Contain("differs from the expected output");
        steps[1].VerifierMessage.Should().Be("Ground truth: the fixed lab's stdout matches the expected output.");
        steps[2].VerifierMessage.Should().Contain("ground-truth gate on the re-run already established correctness");
        host.Engine.Verify(e => e.JudgeAsync(It.IsAny<PromptEnvelope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, "a graded lab never needs a model judgement");
    }

    [Fact]
    public async Task Without_an_expected_output_the_explanation_is_judged_and_the_score_is_stored_as_evidence()
    {
        using var host = Host(new FakeSandbox(_ => LabSpecs.Success("ok\n")));
        EngineStubs.PlanReturns(host.Engine, LabSpecs.ShortPlan(LabPhases.Rerun));
        DecideSequence(host, Call(LabSpecs.RunCall("toolu_1", LabSpecs.FixedProgram)), Text("The using directive named a package that is not referenced; System.Text.Json is built in."));
        string? rubricSeen = null;
        PromptEnvelope? judgeEnvelope = null;
        host.Engine.Setup(e => e.JudgeAsync(It.IsAny<PromptEnvelope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<PromptEnvelope, string, CancellationToken>((e, r, _) => { judgeEnvelope = e; rubricSeen = r; })
            .ReturnsAsync(new JudgementResult(true, 0.85, "Accurate cause, beginner-friendly wording.", new ModelUsage("claude-opus-5-5", 300, 50), "{\"judge\":1}", new ModelCallMetadata("Checked the cause against the compiler output.", "end_turn", "high", 1200)));

        var run = await host.CreateRunAsync();
        var (final, stages) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusCompleted);
        stages.Should().NotContain(AgentStage.Recover);
        rubricSeen.Should().Contain("Fit to the learner's level (Beginner)");
        judgeEnvelope!.Stage.Should().Be(AgentStage.Verify);
        judgeEnvelope.Messages.Count(m => m.Role == PromptMessage.AssistantRole).Should().Be(3, "the plan and both steps are in the transcript the judge sees");

        var explain = await host.QueryAsync(db => db.AgentSteps.SingleAsync(s => s.RunId == run.Id && s.PlanStepIndex == 1));
        explain.Status.Should().Be(AgentStep.StatusPassed);
        explain.VerifierMessage.Should().Be("Judged step 1 passed (score 0.85, minimum 0.70): Accurate cause, beginner-friendly wording.");

        var traces = await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id && t.StepId == explain.Id).OrderBy(t => t.Id).ToListAsync());
        var judgeCall = traces.Should().ContainSingle(t => t.Kind == AgentTrace.KindModelCall && t.Stage == AgentStage.Verify).Subject;
        judgeCall.InputTokens.Should().Be(300);
        judgeCall.ReasoningSummary.Should().Be("Checked the cause against the compiler output.");
        judgeCall.Effort.Should().Be("high");
        judgeCall.CostUsd.Should().BeGreaterThan(0);
        var judgement = traces.Should().ContainSingle(t => t.Kind == AgentTrace.KindJudgement).Subject;
        using var payload = JsonDocument.Parse(judgement.Payload);
        payload.RootElement.GetProperty("score").GetDouble().Should().Be(0.85);
        payload.RootElement.GetProperty("passed").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("rationale").GetString().Should().Contain("beginner-friendly");
        payload.RootElement.GetProperty("phase").GetString().Should().Be(LabPhases.Explain);
        payload.RootElement.GetProperty("rubric").GetString().Should().Contain("Correctness of the diagnosis");
        payload.RootElement.GetProperty("minimumScore").GetDouble().Should().Be(0.7);

        final.TotalInputTokens.Should().Be(120 * 3 + 300, "plan, two Act turns and the judge call are all charged");
        final.TotalOutputTokens.Should().Be(40 * 3 + 50);
    }

    [Fact]
    public async Task A_failed_judgement_fails_the_step_with_the_rationale_and_is_retried()
    {
        using var host = Host(new FakeSandbox(_ => LabSpecs.Success("ok\n")));
        EngineStubs.PlanReturns(host.Engine, LabSpecs.ShortPlan(LabPhases.Rerun));
        DecideSequence(host, Call(LabSpecs.RunCall("toolu_1", LabSpecs.FixedProgram)), Text("It was broken, now it works."), Text("Newtonsoft.Json is a separate package; the fix uses the built-in System.Text.Json instead."));
        var judgeCalls = 0;
        host.Engine.Setup(e => e.JudgeAsync(It.IsAny<PromptEnvelope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++judgeCalls == 1
                ? new JudgementResult(false, 0.2, "No cause is named; a beginner learns nothing.", EngineStubs.Usage, "{}")
                : new JudgementResult(true, 0.9, "Names the cause and the fix plainly.", EngineStubs.Usage, "{}"));

        var run = await host.CreateRunAsync();
        var (final, stages) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusCompleted);
        final.Budget.RetriesLeft.Should().Be(1);
        stages.Should().ContainInOrder(AgentStage.Verify, AgentStage.Recover, AgentStage.Act, AgentStage.Observe, AgentStage.Verify, AgentStage.Learn);
        var attempts = await host.QueryAsync(db => db.AgentSteps.Where(s => s.RunId == run.Id && s.PlanStepIndex == 1).OrderBy(s => s.Attempt).ToListAsync());
        attempts.Select(s => s.Status).Should().Equal(AgentStep.StatusFailed, AgentStep.StatusPassed);
        attempts[0].VerifierMessage.Should().Be("Judged step 1 failed (score 0.20, minimum 0.70): No cause is named; a beginner learns nothing.");
        (await host.QueryAsync(db => db.AgentTraces.CountAsync(t => t.RunId == run.Id && t.Kind == AgentTrace.KindJudgement))).Should().Be(2);
        judgeCalls.Should().Be(2);
    }

    [Fact]
    public async Task The_pass_threshold_is_enforced_by_Verify_not_by_the_judge_s_pass_flag()
    {
        using var host = Host(new FakeSandbox(_ => LabSpecs.Success("ok\n")));
        EngineStubs.PlanReturns(host.Engine, LabSpecs.ShortPlan(LabPhases.Rerun));
        DecideSequence(host, Call(LabSpecs.RunCall("toolu_1", LabSpecs.FixedProgram)), Text("Some words."), Text("Better words."), Text("Best words."));
        var judgeCalls = 0;
        host.Engine.Setup(e => e.JudgeAsync(It.IsAny<PromptEnvelope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++judgeCalls switch
            {
                1 => new JudgementResult(true, 0.2, "The model says pass, the score says otherwise.", EngineStubs.Usage, "{}"),
                2 => new JudgementResult(true, double.NaN, "No number.", EngineStubs.Usage, "{}"),
                _ => new JudgementResult(true, 0.7, "Exactly at the minimum.", EngineStubs.Usage, "{}"),
            });

        var run = await host.CreateRunAsync(taskSpec: LabSpecs.Json());
        var (final, _) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusCompleted);
        final.Budget.RetriesLeft.Should().Be(0, "two judged attempts failed before one met the threshold");
        var attempts = await host.QueryAsync(db => db.AgentSteps.Where(s => s.RunId == run.Id && s.PlanStepIndex == 1).OrderBy(s => s.Attempt).ToListAsync());
        attempts.Select(s => s.Status).Should().Equal(AgentStep.StatusFailed, AgentStep.StatusFailed, AgentStep.StatusPassed);
        attempts[0].VerifierMessage.Should().StartWith("Judged step 1 failed (score 0.20, minimum 0.70)");
        attempts[1].VerifierMessage.Should().Contain("invalid score");
        attempts[2].VerifierMessage.Should().StartWith("Judged step 1 passed (score 0.70, minimum 0.70)");
    }
}
