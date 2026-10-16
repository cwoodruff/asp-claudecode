using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tasks;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Execution;

namespace Intelligently.UnitTests.Agents;

/// <summary>Canned <c>LabErrorDiagnosis</c> specs, plans and sandbox calls for the pilot-task tests.</summary>
internal static class LabSpecs
{
    public const string Entrypoint = "Program.cs";
    public const string CourseFilePath = "Helpers.cs";

    public const string BrokenProgram = "using System;\nusing Newtonsoft.Json;\n\nConsole.WriteLine(JsonConvert.SerializeObject(new { ok = true }));\n";
    public const string FixedProgram = "using System;\nusing System.Text.Json;\n\nConsole.WriteLine(JsonSerializer.Serialize(new { ok = true }));\n";
    public const string CourseFile = "public static class Helpers\n{\n    public static int Twice(int x) => x * 2;\n}\n";
    public const string ErrorText = "error CS0246: The type or namespace name 'Newtonsoft' could not be found (are you missing a using directive or an assembly reference?)";
    public const string ExpectedOutput = "{\"ok\":true}";

    public static LabErrorDiagnosisSpec Spec(
        int courseId = 7,
        string? expectedOutput = null,
        LearnerLevel level = LearnerLevel.Beginner,
        IReadOnlyList<LabSpecFile>? courseFiles = null,
        string entrypoint = Entrypoint,
        string language = "csharp",
        string program = BrokenProgram,
        string errorText = ErrorText) =>
        new(courseId, 3, language, [new LabSpecFile(Entrypoint, program)], courseFiles, entrypoint, errorText, expectedOutput, level);

    public static string Json(
        int courseId = 7,
        string? expectedOutput = null,
        LearnerLevel level = LearnerLevel.Beginner,
        IReadOnlyList<LabSpecFile>? courseFiles = null,
        string entrypoint = Entrypoint,
        string language = "csharp",
        string program = BrokenProgram,
        string errorText = ErrorText) =>
        JsonSerializer.Serialize(Spec(courseId, expectedOutput, level, courseFiles, entrypoint, language, program, errorText));

    /// <summary>A valid spec for course 7 without an expected output (the default run of the test host).</summary>
    public static readonly string Default = Json();

    /// <summary>A spec with a course-provided helper file.</summary>
    public static IReadOnlyList<LabSpecFile> WithCourseFile => [new LabSpecFile(CourseFilePath, CourseFile)];

    /// <summary>The full template plan: reproduce → diagnose → propose_fix → rerun → explain.</summary>
    public static AgentPlan TemplatePlan() => new()
    {
        Goal = "Diagnose and fix the learner's CS0246 build error.",
        Steps =
        [
            new PlanStep { Index = 0, Phase = LabPhases.Reproduce, Description = "Run the submitted files to reproduce CS0246.", Tool = RunLabCodeTool.ToolName, SuccessCriteria = "The build fails with CS0246." },
            new PlanStep { Index = 1, Phase = LabPhases.Diagnose, Description = "Explain why Newtonsoft is not found.", Tool = null, SuccessCriteria = "The cause names the missing package reference." },
            new PlanStep { Index = 2, Phase = LabPhases.ProposeFix, Description = "Replace Newtonsoft.Json with System.Text.Json.", Tool = null, SuccessCriteria = "The changed lines are stated." },
            new PlanStep { Index = 3, Phase = LabPhases.Rerun, Description = "Run the fixed Program.cs.", Tool = RunLabCodeTool.ToolName, SuccessCriteria = "Exit code 0 and the expected output." },
            new PlanStep { Index = 4, Phase = LabPhases.Explain, Description = "Explain the error and the fix to a beginner.", Tool = null, SuccessCriteria = "A beginner-level explanation." },
        ],
    };

    /// <summary>A one-step plan that re-runs the lab (the sandbox step must carry a phase).</summary>
    public static AgentPlan SandboxPlan(string phase = LabPhases.Rerun) => new()
    {
        Goal = "Run the lab.",
        Steps = [new PlanStep { Index = 0, Phase = phase, Description = "Run the lab.", Tool = RunLabCodeTool.ToolName, SuccessCriteria = "The sandbox result matches the phase." }],
    };

    /// <summary>A plan whose only steps are a tagged sandbox run and an explanation.</summary>
    public static AgentPlan ShortPlan(string firstPhase = LabPhases.Rerun) => new()
    {
        Goal = "Check the lab.",
        Steps =
        [
            new PlanStep { Index = 0, Phase = firstPhase, Description = "Run the lab.", Tool = RunLabCodeTool.ToolName, SuccessCriteria = "The sandbox result matches the phase." },
            new PlanStep { Index = 1, Phase = LabPhases.Explain, Description = "Explain.", Tool = null, SuccessCriteria = "An explanation." },
        ],
    };

    /// <summary>A <c>run_lab_code</c> call submitting <paramref name="program"/> as Program.cs plus any course files.</summary>
    public static ToolCall RunCall(string id, string program, string? expectedOutput = null, IReadOnlyList<LabSpecFile>? courseFiles = null, string language = "csharp", string entrypoint = Entrypoint)
    {
        var files = new List<object> { new { path = Entrypoint, content = program } };
        foreach (var file in courseFiles ?? [])
            files.Add(new { path = file.Path, content = file.Content });
        return new ToolCall(id, RunLabCodeTool.ToolName, JsonSerializer.SerializeToElement(new
        {
            language,
            files,
            entrypoint,
            expected_output = expectedOutput,
        }));
    }

    /// <summary>The sandbox output of a compile failure: exit code 1 and the CS0246 diagnostic on stderr.</summary>
    public static SandboxResult CompileFailure(string sandbox = "fake") =>
        new(sandbox, 1, string.Empty, $"Program.cs(2,7): {ErrorText}\n\nThe build failed. Fix the build errors and run again.\n", false, false, TimeSpan.FromMilliseconds(900));

    public static SandboxResult Success(string stdout, string sandbox = "fake") =>
        new(sandbox, 0, stdout, string.Empty, false, false, TimeSpan.FromMilliseconds(650));
}
