using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tasks;
using Intelligently.Infrastructure.Agents.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// The <c>LabErrorDiagnosis</c> task contract (Phase 6): the spec schema is exported from the record with
/// optional properties, a spec is validated against the schema and the task's rules at Interpret, and a
/// malformed spec fails the run with the reasons before any model call.
/// </summary>
public class LabErrorDiagnosisSpecTests
{
    private static readonly LabErrorDiagnosisContract Contract = new();

    private static AgentRun Run(string spec, int courseId = 7) => new()
    {
        UserId = "learner-1",
        CourseId = courseId,
        TaskType = LabErrorDiagnosisSpec.TaskType,
        TaskSpec = spec,
        Principal = RunPrincipal.Learner("learner-1", courseId),
    };

    [Fact]
    public void Schema_is_exported_from_the_record_with_nullable_properties_optional()
    {
        var schema = Contract.SpecSchema;

        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo("course_id", "language", "learner_files", "entrypoint", "error_text", "learner_level");
        var properties = schema.GetProperty("properties");
        properties.GetProperty("expected_output").GetProperty("type").EnumerateArray().Select(e => e.GetString()).Should().Contain("null");
        properties.GetProperty("course_files").GetProperty("type").EnumerateArray().Select(e => e.GetString()).Should().Contain("null");
        properties.GetProperty("learner_level").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal("Beginner", "Intermediate", "Advanced");
        properties.GetProperty("learner_files").GetProperty("items").GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("path", "content");
        properties.GetProperty("error_text").GetProperty("description").GetString().Should().Contain("error the learner saw");
    }

    [Fact]
    public void Registry_finds_the_contract_by_task_type_case_insensitively()
    {
        var registry = new TaskTypeRegistry([Contract]);

        registry.Find("LabErrorDiagnosis").Should().BeSameAs(Contract);
        registry.Find("laberrordiagnosis").Should().BeSameAs(Contract);
        registry.Find("NoteCleanup").Should().BeNull();
        registry.Find(null).Should().BeNull();
        registry.Contracts.Should().ContainSingle();
    }

    [Fact]
    public void A_complete_spec_is_valid_and_optional_properties_may_be_omitted()
    {
        Contract.ValidateSpec(Run(LabSpecs.Json(expectedOutput: "{\"ok\":true}", courseFiles: LabSpecs.WithCourseFile))).IsValid.Should().BeTrue();

        var minimal = """
            {"course_id":7,"language":"python","learner_files":[{"path":"lab.py","content":"print(1/0)"}],"entrypoint":"lab.py","error_text":"ZeroDivisionError","learner_level":"Intermediate"}
            """;
        var validation = Contract.ValidateSpec(Run(minimal));
        validation.IsValid.Should().BeTrue(validation.Summary);
        var spec = LabErrorDiagnosisContract.TryParse(Run(minimal))!;
        spec.ModuleId.Should().BeNull();
        spec.CourseFiles.Should().BeNull();
        spec.HasExpectedOutput.Should().BeFalse();
        spec.LearnerLevel.Should().Be(LearnerLevel.Intermediate);
    }

    [Theory]
    [InlineData("""{"course_id":7}""", "required")]
    [InlineData("""{"course_id":"seven","language":"csharp","learner_files":[],"entrypoint":"Program.cs","error_text":"x","learner_level":"Beginner"}""", "course_id")]
    [InlineData("""{"course_id":7,"language":"csharp","learner_files":[{"path":"Program.cs","content":""}],"entrypoint":"Program.cs","error_text":"x","learner_level":"Guru"}""", "learner_level")]
    [InlineData("""{"course_id":7,"language":"csharp","learner_files":[{"path":"Program.cs","content":""}],"entrypoint":"Program.cs","error_text":"x","learner_level":"Beginner","extra":1}""", "extra")]
    public void A_spec_that_violates_the_schema_is_rejected_with_the_location(string spec, string expectedInError)
    {
        var validation = Contract.ValidateSpec(Run(spec));

        validation.IsValid.Should().BeFalse();
        validation.Summary.Should().Contain(expectedInError);
    }

    [Fact]
    public void Semantic_rules_name_every_problem()
    {
        var wrongCourse = Contract.ValidateSpec(Run(LabSpecs.Json(courseId: 8)));
        wrongCourse.Errors.Should().ContainSingle().Which.Should().Be("course_id 8 does not match the run's course 7.");

        var noEntrypoint = Contract.ValidateSpec(Run(LabSpecs.Json(entrypoint: "Missing.cs")));
        noEntrypoint.Summary.Should().Contain("entrypoint 'Missing.cs' is not among the learner or course files");

        var entrypointInCourseFile = Contract.ValidateSpec(Run(LabSpecs.Json(entrypoint: LabSpecs.CourseFilePath, courseFiles: LabSpecs.WithCourseFile)));
        entrypointInCourseFile.IsValid.Should().BeTrue("the entrypoint may be a course file");

        var duplicate = Contract.ValidateSpec(Run(LabSpecs.Json(courseFiles: [new LabSpecFile(LabSpecs.Entrypoint, "dup")])));
        duplicate.Summary.Should().Contain("appears more than once");

        var noFiles = Contract.ValidateSpec(Run("""{"course_id":7,"language":"csharp","learner_files":[],"entrypoint":"Program.cs","error_text":"x","learner_level":"Beginner"}"""));
        noFiles.Summary.Should().Contain("learner_files must contain at least one file").And.Contain("entrypoint 'Program.cs' is not among");

        var noError = Contract.ValidateSpec(Run(LabSpecs.Json(errorText: "  ")));
        noError.Summary.Should().Contain("error_text is empty");
    }

    [Fact]
    public async Task Interpret_fails_a_malformed_lab_spec_with_the_reason_and_never_calls_the_model()
    {
        using var host = new AgentTestHost();
        var run = await host.CreateRunAsync(taskSpec: """{"course_id":7,"language":"csharp","learner_files":[{"path":"Program.cs","content":""}],"entrypoint":"Other.cs","error_text":"","learner_level":"Beginner"}""");

        var (final, stages) = await host.RunToEndAsync(run.Id);

        stages.Should().Equal(AgentStage.Interpret, AgentStage.Failed);
        final.Status.Should().Be(AgentRun.StatusFailed);
        final.StatusReason.Should().StartWith("Task spec for LabErrorDiagnosis is invalid:")
            .And.Contain("entrypoint 'Other.cs' is not among the learner or course files")
            .And.Contain("error_text is empty");
        host.Engine.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Interpret_accepts_a_valid_lab_spec_and_leaves_untyped_task_types_loose()
    {
        using var host = new AgentTestHost();
        EngineStubs.PlanFails(host.Engine, "stop here");

        var typed = await host.CreateRunAsync();
        (await host.StepAsync(typed.Id)).StatusReason.Should().Contain("LabErrorDiagnosis spec valid");

        var untyped = await host.CreateRunAsync(taskType: "NoteCleanup", taskSpec: """{"noteId":42}""");
        (await host.StepAsync(untyped.Id)).StatusReason.Should().Contain("untyped task");
    }

    [Fact]
    public async Task The_planner_receives_the_template_as_structured_guidance_and_the_plan_schema_carries_the_phase()
    {
        using var host = new AgentTestHost();
        PromptEnvelope? planEnvelope = null;
        host.Engine.Setup(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<PromptEnvelope, CancellationToken>((e, _) => planEnvelope = e)
            .ReturnsAsync(new PlanResult(null, EngineStubs.Usage, "{}", "stop"));
        var run = await host.CreateRunAsync();

        await host.StepAsync(run.Id);
        await host.StepAsync(run.Id);

        var instruction = planEnvelope!.Messages.Last(m => m.Section == PromptSection.Instruction).Text;
        instruction.Should().Contain("Plan template for this task type")
            .And.Contain("1. reproduce:").And.Contain("2. diagnose:").And.Contain("3. propose_fix:").And.Contain("4. rerun:").And.Contain("5. explain:")
            .And.Contain("Tool: run_lab_code").And.Contain("set each step's \"phase\"")
            .And.Contain("Course-provided files are read-only");
        var planSchema = Intelligently.Infrastructure.Agents.Intelligence.AgentJsonSchema.ForNode<AgentPlan>();
        planSchema["properties"]!["steps"]!["items"]!["properties"]!["phase"].Should().NotBeNull();
        LabErrorDiagnosisContract.Template.Steps.Select(s => s.Phase).Should().Equal(LabPhases.All);
    }

    [Fact]
    public void Plans_that_break_the_template_constraints_are_rejected()
    {
        var run = Run(LabSpecs.Default);

        Contract.ValidatePlan(run, LabSpecs.TemplatePlan()).Should().BeNull();
        Contract.ValidatePlan(run, EngineStubs.LookupPlan()).Should().BeNull("untagged plans without a sandbox step are fine");

        var unknownPhase = LabSpecs.TemplatePlan() with { Steps = [LabSpecs.TemplatePlan().Steps[0] with { Phase = "guess" }] };
        Contract.ValidatePlan(run, unknownPhase).Should().Contain("phase 'guess'");

        var sandboxInDiagnose = LabSpecs.TemplatePlan() with { Steps = [LabSpecs.TemplatePlan().Steps[0] with { Phase = LabPhases.Diagnose }] };
        Contract.ValidatePlan(run, sandboxInDiagnose).Should().Contain("calls run_lab_code in phase 'diagnose'");

        var tooLong = LabSpecs.TemplatePlan() with
        {
            Steps = Enumerable.Range(0, LabErrorDiagnosisContract.MaxPlanSteps + 1)
                .Select(i => new PlanStep { Index = i, Description = "d", SuccessCriteria = "s" }).ToList(),
        };
        Contract.ValidatePlan(run, tooLong).Should().Contain("at most");
    }

    [Fact]
    public async Task A_plan_that_violates_the_template_goes_to_Recover_with_the_problem()
    {
        using var host = new AgentTestHost();
        var bad = LabSpecs.ShortPlan(firstPhase: LabPhases.ProposeFix);
        EngineStubs.PlanReturns(host.Engine, bad);
        var run = await host.CreateRunAsync();

        await host.StepAsync(run.Id);
        var planned = await host.StepAsync(run.Id);

        planned.Stage.Should().Be(AgentStage.Recover);
        planned.StatusReason.Should().Contain("calls run_lab_code in phase 'propose_fix'");
    }

    [Fact]
    public void Unified_diff_shows_the_changed_lines_with_context()
    {
        var diff = UnifiedDiff.Create("Program.cs", LabSpecs.BrokenProgram, LabSpecs.FixedProgram)!;

        diff.Should().StartWith("--- a/Program.cs\n+++ b/Program.cs\n@@ -1,4 +1,4 @@");
        diff.Should().Contain("-using Newtonsoft.Json;").And.Contain("+using System.Text.Json;");
        diff.Should().Contain(" using System;", "unchanged lines are context");
        UnifiedDiff.Create("Program.cs", "same\n", "same\n").Should().BeNull();
        UnifiedDiff.Create("New.cs", null, "line\n")!.Should().StartWith("--- /dev/null\n+++ b/New.cs").And.Contain("+line");
    }
}
