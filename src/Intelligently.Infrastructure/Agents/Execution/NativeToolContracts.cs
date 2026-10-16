using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Intelligently.Infrastructure.Agents.Execution;

// One C# record per tool input and output (plan section 1.1, "contracts are C# types"): the JSON Schema
// is exported from the record by System.Text.Json.Schema.JsonSchemaExporter through AgentJsonSchema, the
// same record deserializes the model's arguments, and the output record is what the dispatcher validates.

/// <summary>One source file submitted to <c>run_lab_code</c>.</summary>
public sealed record LabFile(
    [property: JsonPropertyName("path"), Description("Relative path of the file inside the working directory, e.g. Program.cs.")]
    string Path,
    [property: JsonPropertyName("content"), Description("Full text content of the file.")]
    string Content);

/// <summary>Input contract of <c>run_lab_code</c>.</summary>
public sealed record RunLabCodeInput(
    [property: JsonPropertyName("language"), Description("Language of the lab: csharp (single .cs file), csproj (project file entrypoint), fsharp (.fsx script), shell (sh script) or python.")]
    string Language,
    [property: JsonPropertyName("files"), Description("All files the program needs, including the entrypoint.")]
    IReadOnlyList<LabFile> Files,
    [property: JsonPropertyName("entrypoint"), Description("Path (among files) that the language's runner is given, e.g. Program.cs or lab.csproj.")]
    string Entrypoint,
    [property: JsonPropertyName("expected_output"), Description("Expected stdout for a graded lab, or null when there is no known answer; compared after trimming trailing whitespace.")]
    string? ExpectedOutput);

/// <summary>Output contract of <c>run_lab_code</c>: the normalized sandbox result.</summary>
public sealed record RunLabCodeOutput(
    [property: JsonPropertyName("sandbox")] string Sandbox,
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    [property: JsonPropertyName("stdout")] string Stdout,
    [property: JsonPropertyName("stderr")] string Stderr,
    [property: JsonPropertyName("timed_out")] bool TimedOut,
    [property: JsonPropertyName("memory_exceeded")] bool MemoryExceeded,
    [property: JsonPropertyName("output_truncated")] bool OutputTruncated,
    [property: JsonPropertyName("duration_seconds")] double DurationSeconds,
    [property: JsonPropertyName("succeeded")] bool Succeeded,
    [property: JsonPropertyName("matches_expected_output")] bool? MatchesExpectedOutput,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>Priority of an instructor escalation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EscalationPriority
{
    Low,
    Medium,
    High,
}

/// <summary>Input contract of <c>create_escalation</c>.</summary>
public sealed record CreateEscalationInput(
    [property: JsonPropertyName("course_id"), Description("Course the escalation belongs to; must be the run's course.")]
    int CourseId,
    [property: JsonPropertyName("module_id"), Description("Module within the course, or null.")]
    int? ModuleId,
    [property: JsonPropertyName("question"), Description("The learner's question or problem, restated for the instructor.")]
    string Question,
    [property: JsonPropertyName("reason"), Description("Why the agent cannot resolve this itself.")]
    string Reason,
    [property: JsonPropertyName("priority"), Description("Low, Medium or High.")]
    EscalationPriority Priority);

/// <summary>Output contract of <c>create_escalation</c>.</summary>
public sealed record CreateEscalationOutput(
    [property: JsonPropertyName("escalation_id")] int EscalationId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("priority")] string Priority);

/// <summary>Channel a learner notification is sent on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationChannel
{
    InApp,
    Email,
}

/// <summary>Input contract of <c>notify_learner</c>.</summary>
public sealed record NotifyLearnerInput(
    [property: JsonPropertyName("course_id"), Description("Course the notification concerns; must be the run's course.")]
    int CourseId,
    [property: JsonPropertyName("channel"), Description("InApp or Email.")]
    NotificationChannel Channel,
    [property: JsonPropertyName("message"), Description("The message to deliver to the learner, written at their level.")]
    string Message);

/// <summary>Output contract of <c>notify_learner</c>.</summary>
public sealed record NotifyLearnerOutput(
    [property: JsonPropertyName("event_id")] long EventId,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("status")] string Status);
