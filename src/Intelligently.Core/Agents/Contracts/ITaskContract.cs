using System.Text.Json;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>Outcome of validating a task spec against its contract.</summary>
public sealed record TaskSpecValidation(IReadOnlyList<string> Errors)
{
    public static readonly TaskSpecValidation Valid = new([]);
    public static TaskSpecValidation Invalid(params string[] errors) => new(errors);

    public bool IsValid => Errors.Count == 0;
    public string Summary => string.Join("; ", Errors);
}

/// <summary>One phase of a <see cref="PlanTemplate"/>: what a plan step of that phase must do and how it is verified.</summary>
public sealed record PlanTemplateStep(string Phase, string Description, string? Tool, string SuccessCriteria);

/// <summary>
/// Structured guidance handed to the planner for a task type: the ordered phases a plan should cover,
/// the tool each phase normally uses and the success criteria Verify will apply. It constrains the shape
/// of the steps; the planner still decides the plan.
/// </summary>
public sealed record PlanTemplate(string Summary, IReadOnlyList<PlanTemplateStep> Steps, IReadOnlyList<string> Constraints)
{
    public bool HasPhase(string? phase) => phase is not null && Steps.Any(s => string.Equals(s.Phase, phase, StringComparison.Ordinal));
}

/// <summary>
/// Everything a contract sees when verifying one step: the run, the plan step it fulfils, the step row, the
/// tool calls the model made and their normalized results, and the earlier steps of the same plan revision
/// (so a step can depend on what an earlier phase established).
/// </summary>
public sealed record StepVerification(
    AgentRun Run,
    AgentPlan Plan,
    PlanStep PlanStep,
    AgentStep Step,
    IReadOnlyList<ToolCall> Calls,
    IReadOnlyList<ToolResult> Results,
    IReadOnlyList<AgentStep> PriorSteps)
{
    /// <summary>The plan step an earlier step of this revision fulfilled, or null when it is outside the plan.</summary>
    public PlanStep? PlanStepOf(AgentStep step) =>
        step.PlanRevision == Plan.Revision && step.PlanStepIndex >= 0 && step.PlanStepIndex < Plan.Steps.Count ? Plan.Steps[step.PlanStepIndex] : null;
}

/// <summary>A model judgement request: the rubric and the lowest score that counts as a pass (enforced by Verify, not left to the model).</summary>
public sealed record JudgementSpec(string Rubric, double MinimumScore);

/// <summary>A deterministic verdict on a step.</summary>
public sealed record StepVerdict(bool Passed, string Message)
{
    public static StepVerdict Pass(string message) => new(true, message);
    public static StepVerdict Fail(string message) => new(false, message);
}

/// <summary>What a completed run shows the learner: the explanation and, when files changed, the fix as a unified diff.</summary>
public sealed record LearnerOutput(string? Explanation, string? FixDiff);

/// <summary>
/// The typed contract of one task type (plan section 1.1, "contracts are C# types"): the spec schema and
/// its validation (Interpret), the plan template and plan constraints (Plan), the deterministic
/// assertions, ground-truth gate and judgement rubric of a step (Verify, plan section 2.4), what counts as
/// a change to protected course material (Control Plane), and the learner-facing output (Learn).
/// </summary>
public interface ITaskContract
{
    /// <summary>The <c>AgentRun.TaskType</c> value this contract applies to.</summary>
    string TaskType { get; }

    /// <summary>JSON Schema of the task spec, exported from the spec record.</summary>
    JsonElement SpecSchema { get; }

    /// <summary>Validates the run's spec against the schema and the task's semantic rules.</summary>
    TaskSpecValidation ValidateSpec(AgentRun run);

    /// <summary>The plan template given to the planner, or null when the task has none.</summary>
    PlanTemplate? PlanTemplate { get; }

    /// <summary>The rubric a plan candidate is scored against (test-time compute), or null for the generic one.</summary>
    string? PlanJudgementRubric(AgentRun run);

    /// <summary>Checks a produced plan against the template's constraints; returns the problem, or null when the plan is acceptable.</summary>
    string? ValidatePlan(AgentRun run, AgentPlan plan);

    /// <summary>
    /// Deterministic verification of a step (plan section 2.4, contracts 2 and 3: exit code, compiler output,
    /// ground truth). Returns a verdict, or null when the deterministic checks cannot decide.
    /// </summary>
    StepVerdict? VerifyStep(StepVerification context);

    /// <summary>The rubric and pass threshold for a model judgement of the step when <see cref="VerifyStep"/> could not decide, or null when the step passes without one.</summary>
    JudgementSpec? JudgementRubric(StepVerification context);

    /// <summary>
    /// Describes how a proposed tool call changes protected course material (a course-provided file), or
    /// null when it leaves course material intact. The Control Plane routes such calls to approval.
    /// </summary>
    string? DescribeProtectedChange(AgentRun run, ToolCall call);

    /// <summary>The learner-facing output of a completed run.</summary>
    LearnerOutput ComposeLearnerOutput(AgentRun run, AgentPlan? plan, IReadOnlyList<AgentStep> steps);
}

/// <summary>Looks up the contract of a task type by <c>AgentRun.TaskType</c>. Task types without a contract run untyped.</summary>
public interface ITaskTypeRegistry
{
    IReadOnlyList<ITaskContract> Contracts { get; }
    ITaskContract? Find(string? taskType);
}
