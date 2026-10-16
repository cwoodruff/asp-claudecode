using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Intelligently.Core.Agents.Tasks;

/// <summary>How much the learner already knows; the explanation is pitched at this level.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LearnerLevel
{
    Beginner,
    Intermediate,
    Advanced,
}

/// <summary>One file of a lab submission.</summary>
public sealed record LabSpecFile(
    [property: JsonPropertyName("path"), Description("Relative path of the file inside the lab's working directory, e.g. Program.cs.")]
    string Path,
    [property: JsonPropertyName("content"), Description("Full text content of the file.")]
    string Content);

/// <summary>
/// Task spec of the <c>LabErrorDiagnosis</c> pilot task (plan section 3, Phase 6): the learner's lab files,
/// the files the course provided (read-only for the agent: a fix that changes one needs an instructor's
/// approval), how to run them, the error the learner saw, the expected output when the lab is graded, and
/// the learner's level. The JSON Schema is exported from this record and the spec is validated against it
/// at Interpret; the same record is what the stage handlers deserialize.
/// </summary>
public sealed record LabErrorDiagnosisSpec(
    [property: JsonPropertyName("course_id"), Description("Course the lab belongs to; must be the run's course.")]
    int CourseId,
    [property: JsonPropertyName("module_id"), Description("Module within the course, or null.")]
    int? ModuleId,
    [property: JsonPropertyName("language"), Description("Language of the lab as the sandbox knows it: csharp, csproj, fsharp, shell or python.")]
    string Language,
    [property: JsonPropertyName("learner_files"), Description("The files the learner wrote. A fix may change these.")]
    IReadOnlyList<LabSpecFile> LearnerFiles,
    [property: JsonPropertyName("course_files"), Description("Files provided by the course (starter code, tests, data). The agent must not change them; a fix that does needs instructor approval.")]
    IReadOnlyList<LabSpecFile>? CourseFiles,
    [property: JsonPropertyName("entrypoint"), Description("Path (among the learner or course files) that the language's runner is given, e.g. Program.cs or lab.csproj.")]
    string Entrypoint,
    [property: JsonPropertyName("error_text"), Description("The error the learner saw: compiler output, a stack trace or a description of the wrong behaviour.")]
    string ErrorText,
    [property: JsonPropertyName("expected_output"), Description("Expected stdout for a graded lab, or null when there is no known answer.")]
    string? ExpectedOutput,
    [property: JsonPropertyName("learner_level"), Description("Beginner, Intermediate or Advanced; the explanation is written at this level.")]
    LearnerLevel LearnerLevel)
{
    public const string TaskType = "LabErrorDiagnosis";

    /// <summary>Every file of the submission: learner files first, then course files.</summary>
    [JsonIgnore]
    public IEnumerable<LabSpecFile> AllFiles => LearnerFiles.Concat(CourseFiles ?? []);

    /// <summary>True when the lab has a known answer (an empty string is a known answer: no output), so Verify can use the ground-truth gate instead of a model judgement.</summary>
    [JsonIgnore]
    public bool HasExpectedOutput => ExpectedOutput is not null;

    public bool IsCourseFile(string path) => CourseFiles?.Any(f => string.Equals(f.Path, path, StringComparison.Ordinal)) == true;
}

/// <summary>The phases of the lab-error-diagnosis plan template; each plan step is tagged with one.</summary>
public static class LabPhases
{
    public const string Reproduce = "reproduce";
    public const string Diagnose = "diagnose";
    public const string ProposeFix = "propose_fix";
    public const string Rerun = "rerun";
    public const string Explain = "explain";

    public static readonly IReadOnlyList<string> All = [Reproduce, Diagnose, ProposeFix, Rerun, Explain];

    public static bool IsKnown(string? phase) => phase is not null && All.Contains(phase, StringComparer.Ordinal);
}
