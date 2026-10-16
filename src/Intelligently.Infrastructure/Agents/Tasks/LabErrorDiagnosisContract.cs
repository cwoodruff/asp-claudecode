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
///   every sandbox step is tagged <c>reproduce</c> or <c>rerun</c>, reproduce precedes rerun, explain comes last.</item>
///   <item>Verify (plan section 2.4): deterministic assertions on the sandbox result (tool error, timeout,
///   memory kill, the spec's language and entrypoint, exit code, compiler output) run first; <c>reproduce</c> must
///   run the submitted files unchanged and show the error; a <c>rerun</c> step passes the ground-truth gate when
///   the spec has an expected output; <c>explain</c> on a graded lab needs a passed re-run behind it, and on an
///   ungraded lab is judged by the model against a rubric for the correctness of the diagnosis and the fit of
///   the explanation to the learner's level, with a minimum score Verify enforces.</item>
///   <item>Control Plane: a sandbox call whose files change a course-provided file (paths compared in canonical
///   form, so an alias such as <c>./Helpers.cs</c> is the same file) is a change to protected material; the
///   <c>course-material-approval</c> rule routes it to an instructor.</item>
///   <item>Learn: the explanation and the fix (a unified diff of the learner's files, deletions included) are
///   stored on the run.</item>
/// </list>
/// </summary>
public sealed partial class LabErrorDiagnosisContract : ITaskContract
{
    public const int MaxPlanSteps = 10;

    /// <summary>The lowest judge score that passes the explanation of an ungraded lab.</summary>
    public const double ExplanationMinimumScore = 0.7;

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
            "Set each step's \"phase\" to the template phase it fulfils; keep the phases in template order, with explain last.",
            $"Every run_lab_code step is a {LabPhases.Reproduce} or a {LabPhases.Rerun} step, and the {LabPhases.Reproduce} step runs the submitted files unchanged with the spec's language and entrypoint.",
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

        // Paths are compared in canonical form, so "./Program.cs" and "Program.cs" are one file.
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in spec.AllFiles)
        {
            var path = LabPaths.Normalize(file.Path);
            if (path is null)
                problems.Add($"file path '{file.Path}' is not a relative path inside the lab.");
            else if (!paths.Add(path))
                problems.Add($"file path '{file.Path}' appears more than once.");
        }

        if (string.IsNullOrWhiteSpace(spec.Entrypoint))
            problems.Add("entrypoint is empty.");
        else if (LabPaths.Normalize(spec.Entrypoint) is not { } entrypoint || !paths.Contains(entrypoint))
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
            if (step.Tool == RunLabCodeTool.ToolName && step.Phase is not (LabPhases.Reproduce or LabPhases.Rerun))
                return $"Plan step {step.Index} calls {RunLabCodeTool.ToolName} with phase '{step.Phase ?? "none"}'; every sandbox step must be a {LabPhases.Reproduce} or a {LabPhases.Rerun} step so Verify knows what the run must show.";
        }

        var firstReproduce = IndexOfPhase(plan, LabPhases.Reproduce);
        var firstRerun = IndexOfPhase(plan, LabPhases.Rerun);
        if (firstRerun >= 0 && firstReproduce >= 0 && firstReproduce > firstRerun)
            return $"Plan step {firstRerun} re-runs the lab before step {firstReproduce} reproduces the error; reproduce first.";

        var explain = plan.Steps.LastOrDefault(s => s.Phase == LabPhases.Explain);
        if (explain is not null && explain.Index != plan.Steps.Count - 1)
            return $"Plan step {explain.Index} explains to the learner but is not the last step; the explanation comes after every tool step.";

        return null;
    }

    private static int IndexOfPhase(AgentPlan plan, string phase) =>
        plan.Steps.FirstOrDefault(s => s.Phase == phase)?.Index ?? -1;

    // ── Verify ───────────────────────────────────────────────────────────────────────────────────

    public StepVerdict? VerifyStep(StepVerification context)
    {
        var spec = TryParse(context.Run);
        if (spec is null) return null;

        var phase = context.PlanStep.Phase;
        var sandboxRuns = context.Results
            .Where(r => r.Name == RunLabCodeTool.ToolName && !r.IsError)
            .Select(r => (Result: r, Call: context.Calls.FirstOrDefault(c => c.Id == r.ToolCallId), Output: ParseOutput(r)))
            .ToList();

        // Contract 2: deterministic assertions on every sandbox result, whatever the phase: the result is a
        // sandbox output, the lab ran to completion, and it was the spec's lab that ran (language, entrypoint).
        foreach (var (_, call, output) in sandboxRuns)
        {
            if (output is null)
                return StepVerdict.Fail($"{RunLabCodeTool.ToolName} returned a result that is not a sandbox output.");
            if (output.Error is not null)
                return StepVerdict.Fail($"{RunLabCodeTool.ToolName} could not run the lab: {output.Error}");
            if (output.TimedOut)
                return StepVerdict.Fail($"The sandbox run timed out after {output.DurationSeconds:0.#}s; a hang is not a result to reason from. Partial stdout: {Snippet(output.Stdout)}");
            if (output.MemoryExceeded)
                return StepVerdict.Fail($"The sandbox run exceeded its memory limit (exit code {output.ExitCode?.ToString() ?? "none"}).");
            if (call is null)
                return StepVerdict.Fail($"The {RunLabCodeTool.ToolName} result cannot be matched to the call that produced it.");
            if (SubmissionProblem(spec, call) is { } problem)
                return StepVerdict.Fail(problem);
        }

        switch (phase)
        {
            case LabPhases.Reproduce when sandboxRuns.Count > 0:
            {
                var (_, call, output) = sandboxRuns[^1];
                if (!IsOriginalSubmission(spec, call!))
                    return StepVerdict.Fail("The reproduce step must run the submitted files unchanged (every learner and course file, same content, nothing added); the submission differs from the task spec.");
                var mismatch = spec.HasExpectedOutput && !OutputMatches(output!.Stdout, spec.ExpectedOutput!);
                var reproduced = output!.ExitCode != 0 || !string.IsNullOrWhiteSpace(output.Stderr) || mismatch;
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
                if (!spec.HasExpectedOutput)
                    return null;   // the model judges the explanation (contract 4)
                // A graded lab is only correct once a re-run of the fix passed the ground-truth gate in this revision.
                var provedByRerun = context.PriorSteps.Any(s => s.Status == AgentStep.StatusPassed && context.PlanStepOf(s)?.Phase == LabPhases.Rerun);
                return provedByRerun
                    ? StepVerdict.Pass("Explanation recorded; the ground-truth gate on the re-run already established correctness.")
                    : StepVerdict.Fail("The lab has an expected output but no re-run of the fix passed the ground-truth gate in this plan; the explanation cannot be accepted without one.");

            default:
                return null;
        }
    }

    public JudgementSpec? JudgementRubric(StepVerification context)
    {
        if (context.PlanStep.Phase != LabPhases.Explain) return null;
        var spec = TryParse(context.Run);
        if (spec is null || spec.HasExpectedOutput) return null;

        var level = spec.LearnerLevel.ToString();
        var rubric =
            $"Judge the most recent step (the explanation to the learner) and the diagnosis it rests on. Score from 0 to 1 and set passed to true when the score is {ExplanationMinimumScore:0.0} or higher. Criteria: " +
            $"(1) Correctness of the diagnosis: the stated root cause is consistent with the error the learner reported ({Snippet(spec.ErrorText, 200)}) and with the sandbox outputs of the reproduce and re-run steps; the fix follows from the cause and the re-run shows it worked. " +
            $"(2) Fit to the learner's level ({level}): vocabulary, depth and assumed knowledge match a {level.ToLowerInvariant()}; terms a {level.ToLowerInvariant()} would not know are explained; nothing is condescending or padded. " +
            "(3) Honesty: the explanation claims nothing the tool outputs do not show. " +
            "Give a one-paragraph rationale that names the strongest and the weakest point.";
        return new JudgementSpec(rubric, ExplanationMinimumScore);
    }

    /// <summary>A sandbox call that did not run the spec's lab: wrong language or entrypoint, or an aliased path.</summary>
    private static string? SubmissionProblem(LabErrorDiagnosisSpec spec, ToolCall call)
    {
        var input = call.Input;
        if (input.ValueKind != JsonValueKind.Object) return $"The {RunLabCodeTool.ToolName} call has no arguments.";

        var language = StringOf(input, "language");
        if (!string.Equals(language, spec.Language, StringComparison.OrdinalIgnoreCase))
            return $"The sandbox ran the lab as '{language}' but the spec's language is '{spec.Language}'.";

        var entrypoint = LabPaths.Normalize(StringOf(input, "entrypoint") ?? string.Empty);
        if (entrypoint is null || !string.Equals(entrypoint, LabPaths.Normalize(spec.Entrypoint), StringComparison.Ordinal))
            return $"The sandbox ran entrypoint '{StringOf(input, "entrypoint")}' but the spec's entrypoint is '{spec.Entrypoint}'.";

        var submitted = SubmittedFiles(input, out var aliased);
        if (aliased is not null)
            return $"The sandbox call submits '{aliased}' more than once under different spellings; one path per file.";
        if (submitted.Count == 0)
            return $"The {RunLabCodeTool.ToolName} call submitted no files.";
        return null;
    }

    /// <summary>True when the call submits exactly the spec's files with their original content.</summary>
    private static bool IsOriginalSubmission(LabErrorDiagnosisSpec spec, ToolCall call)
    {
        var submitted = SubmittedFiles(call.Input, out _);
        var original = spec.AllFiles
            .Select(f => (Path: LabPaths.Normalize(f.Path), f.Content))
            .Where(f => f.Path is not null)
            .ToDictionary(f => f.Path!, f => f.Content, StringComparer.Ordinal);
        if (submitted.Count != original.Count) return false;
        foreach (var (path, content) in submitted)
        {
            if (!original.TryGetValue(path, out var expected) || !string.Equals(Normalize(expected), Normalize(content), StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    // ── Control Plane ────────────────────────────────────────────────────────────────────────────

    public string? DescribeProtectedChange(AgentRun run, ToolCall call)
    {
        if (call.Name != RunLabCodeTool.ToolName) return null;
        var spec = TryParse(run);
        if (spec?.CourseFiles is not { Count: > 0 } courseFiles) return null;

        // Every spelling of a submitted path is compared; an alias that rewrites a course file is a change to it.
        var submitted = SubmittedFileVersions(call.Input);
        if (submitted.Count == 0) return null;

        var changed = new List<string>();
        foreach (var course in courseFiles)
        {
            var path = LabPaths.Normalize(course.Path);
            if (path is null || !submitted.TryGetValue(path, out var versions)) continue;
            if (versions.Any(content => !string.Equals(Normalize(content), Normalize(course.Content), StringComparison.Ordinal)))
                changed.Add(course.Path);
        }
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
            var files = SubmittedFiles(call.Input, out _);
            if (files.Count > 0) return files;
        }
        return null;
    }

    /// <summary>
    /// The fix as the learner would apply it: changed learner files, learner files the fix left out (the
    /// sandbox only had the submitted files, so leaving one out is deleting it), new files, and any course
    /// file the (approved) fix changed.
    /// </summary>
    private static string? Diff(LabErrorDiagnosisSpec spec, IReadOnlyDictionary<string, string> fixedFiles)
    {
        var parts = new List<string>();
        var learnerPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in spec.LearnerFiles)
        {
            var path = LabPaths.Normalize(file.Path) ?? file.Path;
            learnerPaths.Add(path);
            var content = fixedFiles.TryGetValue(path, out var fixedContent) ? fixedContent : null;
            if (UnifiedDiff.Create(path, file.Content, content) is { } changed)
                parts.Add(changed);
        }
        foreach (var (path, content) in fixedFiles.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (learnerPaths.Contains(path)) continue;
            var course = spec.CourseFiles?.FirstOrDefault(f => LabPaths.Normalize(f.Path) == path);
            if (course is not null)
            {
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

    /// <summary>The submitted files by canonical path (the last spelling wins); <paramref name="aliased"/> names a path submitted under two spellings.</summary>
    private static Dictionary<string, string> SubmittedFiles(JsonElement input, out string? aliased)
    {
        aliased = null;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, versions) in SubmittedFileVersions(input))
        {
            if (versions.Count > 1) aliased ??= path;
            files[path] = versions[^1];
        }
        return files;
    }

    /// <summary>Every submitted version of each canonical path, in submission order.</summary>
    private static Dictionary<string, List<string>> SubmittedFileVersions(JsonElement input)
    {
        var files = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("files", out var array) || array.ValueKind != JsonValueKind.Array)
            return files;
        foreach (var file in array.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object) continue;
            if (!file.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String) continue;
            var path = LabPaths.Normalize(pathElement.GetString()!);
            if (path is null) continue;
            var content = file.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
            if (!files.TryGetValue(path, out var versions)) files[path] = versions = [];
            versions.Add(content);
        }
        return files;
    }

    private static string? StringOf(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static bool OutputMatches(string stdout, string expected) =>
        string.Equals(Normalize(stdout).TrimEnd(), Normalize(expected).TrimEnd(), StringComparison.Ordinal);

    private static bool HasText(AgentStep step) => !string.IsNullOrWhiteSpace(step.AssistantText);

    private static string FirstNonEmpty(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a;

    private static string Snippet(string text, int max = OutputSnippetChars)
    {
        var flat = Normalize(text).Trim();
        if (flat.Length == 0) return "(no output)";
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>Compiler and runtime error markers of the sandbox languages: C#/F#/MSBuild diagnostics, Python tracebacks, shell command failures.</summary>
    [GeneratedRegex(@"\berror\s+(CS|FS|MSB|NETSDK)\d{4}\b|Traceback \(most recent call last\)|^\s*\w*(Error|Exception)\b.*$|^sh: .*: not found$|Unhandled exception\.", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex CompilerErrorPattern();
}

/// <summary>
/// Canonical form of a lab file path, shared by the spec, the Control Plane's protected-file check and the
/// learner diff, so <c>./Helpers.cs</c>, <c>Helpers.cs</c> and <c>sub\..\Helpers.cs</c> are one file, as they
/// are once the sandbox writes them. Null for a path that is empty, absolute or escapes the lab directory.
/// </summary>
public static class LabPaths
{
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var unified = path.Trim().Replace('\\', '/');
        if (unified.StartsWith('/') || unified.Length > 1 && unified[1] == ':') return null;

        var segments = new List<string>();
        foreach (var segment in unified.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }
}
