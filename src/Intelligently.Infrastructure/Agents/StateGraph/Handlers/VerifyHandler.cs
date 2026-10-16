using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Data;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>
/// Verify (plan section 2.4), in order: the generic deterministic checks (required tool called, no error
/// results; the dispatcher already validated every result against its output schema), then the task
/// contract's deterministic assertions and ground-truth gate (exit code, compiler output, expected output),
/// and only when those cannot decide a model judgement against the contract's rubric, whose score and
/// rationale are stored as evidence. A failure caused by a policy denial is final for that call, so Recover
/// is told to replan instead of retrying.
/// </summary>
public sealed class VerifyHandler : IStageHandler
{
    private readonly IntelligentlyDbContext _db;
    private readonly ITaskTypeRegistry _tasks;
    private readonly IContextAssembler _context;
    private readonly IReasoningEngine _engine;
    private readonly IEvidenceSink _evidence;
    private readonly ICostAccountant _costs;
    private readonly string _model;

    public VerifyHandler(
        IntelligentlyDbContext db,
        ITaskTypeRegistry tasks,
        IContextAssembler context,
        IReasoningEngine engine,
        IEvidenceSink evidence,
        ICostAccountant costs,
        IOptions<Core.Agents.AgentsOptions> options)
    {
        _db = db;
        _tasks = tasks;
        _context = context;
        _engine = engine;
        _evidence = evidence;
        _costs = costs;
        _model = options.Value.DefaultModel;
    }

    public AgentStage Stage => AgentStage.Verify;

    public async Task<StageOutcome> ExecuteAsync(AgentRun run, CancellationToken ct = default)
    {
        var plan = await StepQueries.CurrentPlanAsync(_db, run, ct);
        var step = await StepQueries.LatestAsync(_db, run, ct);

        if (plan is null || step is null)
            return Fail(run, step, "Nothing to verify: no plan or step recorded.");

        if (run.CurrentStepIndex >= plan.Steps.Count)
            return Fail(run, step, $"Step index {run.CurrentStepIndex} is outside the plan.");

        var planStep = plan.Steps[run.CurrentStepIndex];
        var results = StepQueries.Results(step);

        // 1. Generic deterministic checks.
        if (planStep.Tool is not null)
        {
            if (!results.Any(r => r.Name == planStep.Tool))
                return Fail(run, step, $"Step {planStep.Index} required tool '{planStep.Tool}' but it was not called.");
            var error = results.FirstOrDefault(r => r.IsError);
            if (error is not null)
                return Fail(run, step, $"Tool '{error.Name}' failed: {error.Content}", error);
        }
        else if (results.FirstOrDefault(r => r.IsError) is { } error)
        {
            return Fail(run, step, $"A tool call failed: {error.Content}", error);
        }
        else if (string.IsNullOrWhiteSpace(step.AssistantText) && results.Count == 0)
        {
            return Fail(run, step, $"Step {planStep.Index} produced neither text nor tool results.");
        }

        // 2.–4. The task contract: deterministic assertions, ground-truth gate, then model judgement.
        var message = "Passed deterministic checks.";
        if (_tasks.Find(run.TaskType) is { } contract)
        {
            var context = new StepVerification(run, plan, planStep, step, results);
            var verdict = contract.VerifyStep(context);
            if (verdict is { Passed: false })
                return Fail(run, step, verdict.Message);
            if (verdict is { Passed: true })
            {
                message = verdict.Message;
            }
            else if (contract.JudgementRubric(context) is { } rubric)
            {
                if (run.Budget.IsExhausted)
                    return Exhausted(run, planStep, plan, $"step {planStep.Index} needs a model judgement");

                var judged = await JudgeAsync(run, step, planStep, rubric, ct);
                if (!judged.Passed)
                    return Fail(run, step, $"Judged step {planStep.Index} failed (score {judged.Score:0.00}): {judged.Rationale}");
                message = $"Judged step {planStep.Index} passed (score {judged.Score:0.00}): {judged.Rationale}";
            }
        }

        step.Status = AgentStep.StatusPassed;
        step.VerifierMessage = message;
        step.CompletedAt = DateTime.UtcNow;
        run.RecoveryReason = null;

        if (run.CurrentStepIndex + 1 < plan.Steps.Count)
        {
            // The step passed, but the next one would call the model; a spent hard budget ends the run here.
            if (run.Budget.IsExhausted)
                return Exhausted(run, planStep, plan, $"step {planStep.Index} passed");

            run.CurrentStepIndex++;
            return StageOutcome.To(AgentStage.Act, $"Step {planStep.Index} passed; {plan.Steps.Count - run.CurrentStepIndex} step(s) remain. {message}");
        }

        return StageOutcome.To(AgentStage.Learn, $"Step {planStep.Index} passed; plan complete. {message}");
    }

    /// <summary>Contract 4: one judge call over the run's transcript, charged to the run and stored as a ModelCall row plus a Judgement row.</summary>
    private async Task<JudgementResult> JudgeAsync(AgentRun run, AgentStep step, PlanStep planStep, string rubric, CancellationToken ct)
    {
        var envelope = await _context.BuildAsync(run, Stage, ct);

        JudgementResult result;
        using (_evidence.StartModelCall(run, Stage, _model, "high"))
        {
            result = await _engine.JudgeAsync(envelope, rubric, ct);
            await UsageAccounting.RecordAsync(_costs, _evidence, run, Stage, envelope, result.Usage, result.RawResponse, null, result.Metadata, step.Id, ct);
        }

        await _evidence.RecordAsync(run, new AgentTrace
        {
            Stage = Stage,
            StepId = step.Id,
            Kind = AgentTrace.KindJudgement,
            Model = result.Usage.Model,
            Payload = JsonSerializer.Serialize(new
            {
                stepIndex = planStep.Index,
                phase = planStep.Phase,
                attempt = step.Attempt,
                passed = result.Passed,
                score = result.Score,
                rationale = result.Rationale,
                rubric,
            }),
        }, ct);

        return result;
    }

    private static StageOutcome Exhausted(AgentRun run, PlanStep planStep, AgentPlan plan, string when)
    {
        var budget = run.Budget;
        run.RecoveryReason = RecoveryReasons.BudgetExhausted;
        return StageOutcome.To(AgentStage.Recover,
            $"Step {planStep.Index}: {when} but the hard budget is exhausted (tokens {budget.TokensLeft}, tool calls {budget.ToolCallsLeft}, wall clock {budget.WallClockLeft}); {plan.Steps.Count - run.CurrentStepIndex - 1} step(s) not run.");
    }

    private static StageOutcome Fail(AgentRun run, AgentStep? step, string message, ToolResult? error = null)
    {
        if (step is not null)
        {
            step.Status = AgentStep.StatusFailed;
            step.VerifierMessage = message;
            step.CompletedAt = DateTime.UtcNow;
        }
        run.RecoveryReason = error?.IsPolicyDenial == true ? RecoveryReasons.PolicyDenied : RecoveryReasons.StepFailed;
        return StageOutcome.To(AgentStage.Recover, message);
    }
}
