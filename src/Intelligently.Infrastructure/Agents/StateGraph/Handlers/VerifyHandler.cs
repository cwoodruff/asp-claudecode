using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Data;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>
/// Verify: deterministic checks only (required tool called, no error results).
/// Ground-truth gates and model judgement arrive with later phases. A failure caused by a policy
/// denial is final for that call, so Recover is told to replan instead of retrying.
/// </summary>
public sealed class VerifyHandler : IStageHandler
{
    private readonly IntelligentlyDbContext _db;

    public VerifyHandler(IntelligentlyDbContext db)
    {
        _db = db;
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

        step.Status = AgentStep.StatusPassed;
        step.VerifierMessage = "Passed deterministic checks.";
        step.CompletedAt = DateTime.UtcNow;
        run.RecoveryReason = null;

        if (run.CurrentStepIndex + 1 < plan.Steps.Count)
        {
            // The step passed, but the next one would call the model; a spent hard budget ends the run here.
            if (run.Budget.IsExhausted)
            {
                var budget = run.Budget;
                run.RecoveryReason = RecoveryReasons.BudgetExhausted;
                return StageOutcome.To(AgentStage.Recover,
                    $"Step {planStep.Index} passed but the hard budget is exhausted (tokens {budget.TokensLeft}, tool calls {budget.ToolCallsLeft}, wall clock {budget.WallClockLeft}); {plan.Steps.Count - run.CurrentStepIndex - 1} step(s) not run.");
            }

            run.CurrentStepIndex++;
            return StageOutcome.To(AgentStage.Act, $"Step {planStep.Index} passed; {plan.Steps.Count - run.CurrentStepIndex} step(s) remain.");
        }

        return StageOutcome.To(AgentStage.Learn, $"Step {planStep.Index} passed; plan complete.");
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
