using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tasks;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Execution;
using Intelligently.Infrastructure.Agents.Intelligence;
using Json.Schema;

namespace Intelligently.Infrastructure.Agents.Tasks;

/// <summary>
/// The <c>LabErrorDiagnosis</c> pilot task (plan section 3, Phase 6).
/// <list type="bullet">
///   <item>Interpret: the spec is validated against the schema exported from <see cref="LabErrorDiagnosisSpec"/>
///   (optional properties may be omitted) and the task's own rules (the run's course, files, entrypoint).</item>
///   <item>Plan: the template reproduce → diagnose → propose_fix → rerun → explain is handed to the planner;
///   every step carries a template phase, and <c>run_lab_code</c> is only used to reproduce or re-run.</item>
///   <item>Verify (plan section 2.4): deterministic assertions on the sandbox result (tool error, timeout,
///   memory kill, exit code, compiler output) run first; a <c>rerun</c> step then passes the ground-truth gate
///   when the spec has an expected output; otherwise the <c>explain</c> step is judged by the model against a
///   rubric for the correctness of the diagnosis and the fit of the explanation to the learner's level.</item>
///   <item>Control Plane: a sandbox call whose files change a course-provided file is a change to protected
///   material; the <c>course-material-approval</c> rule routes it to an instructor.</item>
///   <item>Learn: the explanation and the fix (a unified diff of the learner's files) are stored on the run.</item>
/// </list>
/// </summary>
public sealed partial class LabErrorDiagnosisContract : ITaskContract
{
    public const int MaxPlanSteps = 10;
    private const int MaxSchemaErrors = 8;
    private const int OutputSnippetChars = 400;

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(AgentJsonSchema.ForNode<LabErrorDiagnosisSpec>(requireAll: false));
    private static readonly JsonSchema CompiledSchema = JsonSchema.FromText(Schema.GetRawText());

    /// <summary>The plan template; the phases are the names a plan step is tagged with.</summary>
    public static readonly PlanTemplate Template = new(
        "Diagnose a learner's lab error: reproduce it in the sandbox, find the cause, propose the smallest fix to the learner's files, prove the fix by re-running, then explain it at the learner's level.",
        [
            new(LabPhases.Reproduce,
                "Run the learner's files exactly as submitted (learner and course files, same entrypoint and language) with run_lab_code to reproduce the reported error.",
                RunLabCodeTool.ToolName,
                "The sandbox shows the reported error: a non-zero exit code, compiler or runtime error output, or output that differs from the expected output."),
            new(LabPhases.Diagnose,
                "State the root cause of the error from the sandbox output and the code. Use lookup_knowledge when the course knowledge base may document this error; otherwise reason from the evidence.",
                null,
                "A specific root cause tied to a line or construct in the learner's files, consistent with the error text and the sandbox output."),
            new(LabPhases.ProposeFix,
                "Propose the smallest change to the learner's files that removes the cause. Never change course-provided files; if the only fix is in a course file, say so and stop.",
                null,
                "The changed file(s) and lines are stated with the new content."),
            new(LabPhases.Rerun,
                "Run the fixed files with run_lab_code (same language and entrypoint, course files unchanged) to prove the fix.",
                RunLabCodeTool.ToolName,
                "Exit code 0, no compiler errors, and stdout equal to the expected output when the lab has one."),
            new(LabPhases.Explain,
                "Explain the error, its cause and the fix to the learner at their level, in plain text, without calling a tool.",
                null,
                "A plain-language explanation that matches the diagnosis, the fix and the learner's level."),
        ],
        [
            "Set each step's \"phase\" to the template phase it fulfils; keep the phases in template order.",
            $"Use run_lab_code only for the {LabPhases.Reproduce} and {LabPhases.Rerun} phases.",
            "Course-provided files are read-only: a fix that changes one needs an instructor's approval, so prefer fixes in the learner's files.",
            $"Keep the plan to at most {MaxPlanSteps} steps.",
        ]);

    public string TaskType => LabErrorDiagnosisSpec.TaskType;

    public JsonElement SpecSchema => Schema;

    public PlanTemplate? PlanTemplate => Template;

    // ── Interpret ────────────────────────────────────────────────────────────────────────────────

    public TaskSpecValidation ValidateSpec(AgentRun run)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(run.TaskSpec);
        }
        catch (JsonException ex)
        {
            return TaskSpecValidation.Invalid($"Task spec is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var evaluation = CompiledSchema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!evaluation.IsValid)
            {
                var errors = SchemaErrors(evaluation).Take(MaxSchemaErrors).ToList();
                return new TaskSpecValidation(errors.Count == 0 ? ["Task spec does not match the LabErrorDiagnosis schema."] : errors);
            }
        }

        LabErrorDiagnosisSpec? spec;
        try
        {
            spec = JsonSerializer.Deserialize<LabErrorDiagnosisSpec>(run.TaskSpec, AgentJsonSchema.SerializerOptions);
        }
        catch (JsonException ex)
        {
            return TaskSpecValidation.Invalid($"Task spec could not be read as a LabErrorDiagnosis spec: {ex.Message}");
        }
        if (spec is null)
            return TaskSpecValidation.Invalid("Task spec is empty.");

        var problems = new List<string>();
        if (spec.CourseId != run.CourseId)
            problems.Add($"course_id {spec.CourseId} does not match the run's course {run.CourseId}.");
        if (string.IsNullOrWhiteSpace(spec.Language))
            problems.Add("language is empty.");
        if (spec.LearnerFiles.Count == 0)
            problems.Add("learner_files must contain at least one file.");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in spec.AllFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
                problems.Add("every file needs a non-empty path.");
            else if (!paths.Add(file.Path))
                problems.Add($"file path '{file.Path}' appears more than once.");
        }

        if (string.IsNullOrWhiteSpace(spec.Entrypoint))
            problems.Add("entrypoint is empty.");
        else if (!paths.Contains(spec.Entrypoint))
            problems.Add($"entrypoint '{spec.Entrypoint}' is not among the learner or course files.");

        if (string.IsNullOrWhiteSpace(spec.ErrorText))
            problems.Add("error_text is empty: describe the error the learner saw.");

        return problems.Count == 0 ? TaskSpecValidation.Valid : new TaskSpecValidation(problems);
    }

    private static IEnumerable<string> SchemaErrors(EvaluationResults results)
    {
        var nodes = results.Details is { Count: > 0 } details ? details : [results];
        foreach (var node in nodes)
        {
            if (node.Errors is null) continue;
            foreach (var (keyword, message) in node.Errors)
            {
                var location = node.InstanceLocation.ToString();
                yield return $"{(string.IsNullOrEmpty(location) ? "spec" : location)} {keyword}: {message}";
            }
        }
    }

    /// <summary>The run's spec as the record, or null when it does not deserialize (Interpret rejects such runs).</summary>
    public static LabErrorDiagnosisSpec? TryParse(AgentRun run)
    {
        try
        {
            return JsonSerializer.Deserialize<LabErrorDiagnosisSpec>(run.TaskSpec, AgentJsonSchema.SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Plan ─────────────────────────────────────────────────────────────────────────────────────

    public string? PlanJudgementRubric(AgentRun run)
    {
        var level = TryParse(run)?.LearnerLevel.ToString() ?? "unknown";
        return
            "Score the candidate plan from 0 to 1 for a lab error diagnosis. Award points for: " +
            $"(1) covering the template phases in order ({string.Join(" → ", LabPhases.All)}) with each step tagged with its phase; " +
            $"(2) using run_lab_code only to reproduce and to re-run, and never changing course-provided files; " +
            $"(3) success criteria that a verifier can check deterministically (exit code, compiler output, expected output) or, for the explanation, that name the learner's level ({level}); " +
            "(4) concision: no step that does not move the diagnosis forward. " +
            "Set passed to true when the score is 0.6 or higher.";
    }

    public string? ValidatePlan(AgentRun run, AgentPlan plan)
    {
        if (plan.Steps.Count > MaxPlanSteps)
            return $"Plan has {plan.Steps.Count} steps; the lab template allows at most {MaxPlanSteps}.";

        foreach (var step in plan.Steps)
        {
            if (step.Phase is not null && !LabPhases.IsKnown(step.Phase))
                return $"Plan step {step.Index} has phase '{step.Phase}', which is not a template phase ({string.Join(", ", LabPhases.All)}).";
            if (step.Tool == RunLabCodeTool.ToolName && step.Phase is not null && step.Phase is not (LabPhases.Reproduce or LabPhases.Rerun))
                return $"Plan step {step.Index} calls {RunLabCodeTool.ToolName} in phase '{step.Phase}'; the sandbox is only used to {LabPhases.Reproduce} or {LabPhases.Rerun}.";
        }

        return null;
    }

    // ── Verify ───────────────────────────────────────────────────────────────────────────────────

    public StepVerdict? VerifyStep(StepVerification context)
    {
        var spec = TryParse(context.Run);
        if (spec is null) return null;

        var phase = context.PlanStep.Phase;
        var sandboxRuns = context.Results
            .Where(r => r.Name == RunLabCodeTool.ToolName && !r.IsError)
            .Select(r => (Result: r, Output: ParseOutput(r)))
            .ToList();

        // Contract 2: deterministic assertions on every sandbox result, whatever the phase.
        foreach (var (_, output) in sandboxRuns)
        {
            if (output is null)
                return StepVerdict.Fail($"{RunLabCodeTool.ToolName} returned a result that is not a sandbox output.");
            if (output.Error is not null)
                return StepVerdict.Fail($"{RunLabCodeTool.ToolName} could not run the lab: {output.Error}");
            if (output.TimedOut)
                return StepVerdict.Fail($"The sandbox run timed out after {output.DurationSeconds:0.#}s; a hang is not a result to reason from. Partial stdout: {Snippet(output.Stdout)}");
            if (output.MemoryExceeded)
                return StepVerdict.Fail($"The sandbox run exceeded its memory limit (exit code {output.ExitCode?.ToString() ?? "none"}).");
        }

        switch (phase)
        {
            case LabPhases.Reproduce when sandboxRuns.Count > 0:
            {
                var output = sandboxRuns[^1].Output!;
                var mismatch = spec.HasExpectedOutput && !OutputMatches(output.Stdout, spec.ExpectedOutput!);
                var reproduced = output.ExitCode != 0 || !string.IsNullOrWhiteSpace(output.Stderr) || mismatch;
                if (!reproduced)
                    return StepVerdict.Fail($"The reported error did not reproduce: the submitted files exited with code 0, wrote nothing to stderr{(spec.HasExpectedOutput ? " and printed the expected output" : string.Empty)}. Check the files and entrypoint match the submission.");
                var how = output.ExitCode != 0 ? $"exit code {output.ExitCode}" : mismatch ? "output differs from the expected output" : "errors on stderr";
                return StepVerdict.Pass($"Reproduced the reported error ({how}): {Snippet(FirstNonEmpty(output.Stderr, output.Stdout))}");
            }

            case LabPhases.Rerun when sandboxRuns.Count > 0:
            {
                var output = sandboxRuns[^1].Output!;
                if (output.ExitCode != 0)
                    return StepVerdict.Fail($"The fixed lab exited with code {output.ExitCode?.ToString() ?? "none"}: {Snippet(FirstNonEmpty(output.Stderr, output.Stdout))}");
                if (CompilerErrorPattern().Match(output.Stderr + "\n" + output.Stdout) is { Success: true } error)
                    return StepVerdict.Fail($"The fixed lab still reports errors: {Snippet(error.Value)}");
                if (!spec.HasExpectedOutput)
                    return StepVerdict.Pass("The fixed lab ran with exit code 0 and no compiler errors; the lab has no expected output to compare against.");
                return OutputMatches(output.Stdout, spec.ExpectedOutput!)
                    ? StepVerdict.Pass("Ground truth: the fixed lab's stdout matches the expected output.")
                    : StepVerdict.Fail($"Ground truth: the fixed lab's stdout differs from the expected output. Expected: {Snippet(spec.ExpectedOutput!)} Got: {Snippet(output.Stdout)}");
            }

            case LabPhases.Reproduce:
            case LabPhases.Rerun:
                return StepVerdict.Fail($"Phase '{phase}' must run the lab with {RunLabCodeTool.ToolName}, but no sandbox result was recorded.");

            case LabPhases.Diagnose:
            case LabPhases.ProposeFix:
                return HasText(context.Step) || context.Results.Count > 0
                    ? StepVerdict.Pass($"Phase '{phase}' recorded its reasoning.")
                    : StepVerdict.Fail($"Phase '{phase}' produced no text and no tool result.");

            case LabPhases.Explain:
                if (!HasText(context.Step))
                    return StepVerdict.Fail("The explanation step produced no text for the learner.");
                // With an expected output the re-run already established the ground truth; without one the model judges the explanation.
                return spec.HasExpectedOutput
                    ? StepVerdict.Pass("Explanation recorded; the ground-truth gate on the re-run already established correctness.")
                    : null;

            default:
                return null;
        }
    }

    public string? JudgementRubric(StepVerification context)
    {
        if (context.PlanStep.Phase != LabPhases.Explain) return null;
        var spec = TryParse(context.Run);
        if (spec is null || spec.HasExpectedOutput) return null;

        return
            "Judge the most recent step (the explanation to the learner) and the diagnosis it rests on. Score from 0 to 1 and set passed to true when the score is 0.7 or higher. Criteria: " +
            $"(1) Correctness of the diagnosis: the stated root cause is consistent with the error the learner reported ({Snippet(spec.ErrorText, 200)}) and with the sandbox outputs of the reproduce and re-run steps; the fix follows from the cause and the re-run shows it worked. " +
            $"(2) Fit to the learner's level ({spec.LearnerLevel}): vocabulary, depth and assumed knowledge match a {spec.LearnerLevel.ToString().ToLowerInvariant()}; terms a {spec.LearnerLevel.ToString().ToLowerInvariant()} would not know are explained; nothing is condescending or padded. " +
            "(3) Honesty: the explanation claims nothing the tool outputs do not show. " +
            "Give a one-paragraph rationale that names the strongest and the weakest point.";
    }

    // ── Control Plane ────────────────────────────────────────────────────────────────────────────

    public string? DescribeProtectedChange(AgentRun run, ToolCall call)
    {
        if (call.Name != RunLabCodeTool.ToolName) return null;
        var spec = TryParse(run);
        if (spec?.CourseFiles is not { Count: > 0 } courseFiles) return null;

        var submitted = SubmittedFiles(call.Input);
        if (submitted.Count == 0) return null;

        var changed = courseFiles
            .Where(course => submitted.TryGetValue(course.Path, out var content) && !string.Equals(content, course.Content, StringComparison.Ordinal))
            .Select(course => course.Path)
            .ToList();
        return changed.Count == 0
            ? null
            : $"the proposed fix changes course-provided file(s) {string.Join(", ", changed.Select(p => $"'{p}'"))}, which the agent may not change on its own";
    }

    // ── Learn ────────────────────────────────────────────────────────────────────────────────────

    public LearnerOutput ComposeLearnerOutput(AgentRun run, AgentPlan? plan, IReadOnlyList<AgentStep> steps)
    {
        var spec = TryParse(run);
        var passed = steps
            .Where(s => s.Status == AgentStep.StatusPassed)
            .OrderBy(s => s.StartedAt).ThenBy(s => s.Attempt)
            .ToList();

        string? PhaseOf(AgentStep step) =>
            plan is not null && step.PlanRevision == plan.Revision && step.PlanStepIndex < plan.Steps.Count ? plan.Steps[step.PlanStepIndex].Phase : null;

        var explanation = passed.LastOrDefault(s => PhaseOf(s) == LabPhases.Explain && HasText(s))?.AssistantText
                          ?? passed.LastOrDefault(HasText)?.AssistantText;

        string? diff = null;
        if (spec is not null)
        {
            var fixedFiles = passed
                .Where(s => PhaseOf(s) != LabPhases.Reproduce)
                .Select(SuccessfulSubmission)
                .LastOrDefault(files => files is not null);
            if (fixedFiles is not null)
                diff = Diff(spec, fixedFiles);
        }

        return new LearnerOutput(explanation?.Trim(), diff);
    }

    /// <summary>The files of the last successful (exit code 0) sandbox call of a step, or null.</summary>
    private static IReadOnlyDictionary<string, string>? SuccessfulSubmission(AgentStep step)
    {
        var calls = Deserialize<List<ToolCall>>(step.ToolCallsJson) ?? [];
        var results = Deserialize<List<ToolResult>>(step.ToolResultsJson) ?? [];
        foreach (var result in results.AsEnumerable().Reverse())
        {
            if (result.Name != RunLabCodeTool.ToolName || result.IsError) continue;
            var output = ParseOutput(result);
            if (output is null || !output.Succeeded) continue;
            var call = calls.FirstOrDefault(c => c.Id == result.ToolCallId);
            if (call is null) continue;
            var files = SubmittedFiles(call.Input);
            if (files.Count > 0) return files;
        }
        return null;
    }

    private static string? Diff(LabErrorDiagnosisSpec spec, IReadOnlyDictionary<string, string> fixedFiles)
    {
        var parts = new List<string>();
        foreach (var file in spec.LearnerFiles)
        {
            if (fixedFiles.TryGetValue(file.Path, out var content) && UnifiedDiff.Create(file.Path, file.Content, content) is { } changed)
                parts.Add(changed);
        }
        foreach (var (path, content) in fixedFiles.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (spec.LearnerFiles.Any(f => f.Path == path)) continue;
            if (spec.IsCourseFile(path))
            {
                var course = spec.CourseFiles!.First(f => f.Path == path);
                if (UnifiedDiff.Create(path, course.Content, content) is { } courseChange)
                    parts.Add(courseChange);
                continue;
            }
            if (UnifiedDiff.Create(path, null, content) is { } added)
                parts.Add(added);
        }
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> SubmittedFiles(JsonElement input)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("files", out var array) || array.ValueKind != JsonValueKind.Array)
            return files;
        foreach (var file in array.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object) continue;
            if (!file.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) continue;
            var content = file.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
            files[path.GetString()!] = content;
        }
        return files;
    }

    private static RunLabCodeOutput? ParseOutput(ToolResult result)
    {
        try
        {
            return result.Payload is { ValueKind: JsonValueKind.Object } payload
                ? payload.Deserialize<RunLabCodeOutput>(AgentJsonSchema.SerializerOptions)
                : JsonSerializer.Deserialize<RunLabCodeOutput>(result.Content, AgentJsonSchema.SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool OutputMatches(string stdout, string expected) =>
        string.Equals(stdout.Replace("\r\n", "\n").TrimEnd(), expected.Replace("\r\n", "\n").TrimEnd(), StringComparison.Ordinal);

    private static bool HasText(AgentStep step) => !string.IsNullOrWhiteSpace(step.AssistantText);

    private static string FirstNonEmpty(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a;

    private static string Snippet(string text, int max = OutputSnippetChars)
    {
        var flat = text.Replace("\r\n", "\n").Trim();
        if (flat.Length == 0) return "(no output)";
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>Compiler and runtime error markers of the sandbox languages: C#/F#/MSBuild diagnostics, Python tracebacks, shell command failures.</summary>
    [GeneratedRegex(@"\berror\s+(CS|FS|MSB|NETSDK)\d{4}\b|Traceback \(most recent call last\)|^\s*\w*(Error|Exception)\b.*$|^sh: .*: not found$|Unhandled exception\.", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex CompilerErrorPattern();
}
