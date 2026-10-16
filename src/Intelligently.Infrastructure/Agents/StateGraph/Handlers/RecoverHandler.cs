using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Entities.AI;
using Intelligently.Infrastructure.Data;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>
/// Recover: spend exactly one recovery counter and take the matching edge, or
/// escalate to an instructor and fail when the budget is exhausted.
/// Oscillation detection arrives in Phase 2.
/// </summary>
public sealed class RecoverHandler : IStageHandler
{
    private readonly IntelligentlyDbContext _db;
    private readonly IEvidenceSink _evidence;

    public RecoverHandler(IntelligentlyDbContext db, IEvidenceSink evidence)
    {
        _db = db;
        _evidence = evidence;
    }

    public AgentStage Stage => AgentStage.Recover;

    public async Task<StageOutcome> ExecuteAsync(AgentRun run, CancellationToken ct = default)
    {
        var budget = run.Budget;
        var cause = run.StatusReason ?? "unknown failure";

        if (budget.IsExhausted)
            return await EscalateAsync(run, $"Hard budget exhausted (tokens {budget.TokensLeft}, tool calls {budget.ToolCallsLeft}, wall clock {budget.WallClockLeft}). Last failure: {cause}", ct);

        if (run.RecoveryReason == RecoveryReasons.StepFailed && budget.TrySpendRetry())
            return StageOutcome.To(AgentStage.Act, $"Retrying step {run.CurrentStepIndex} ({budget.RetriesLeft} retries left). Cause: {cause}");

        if (budget.TrySpendReplan())
        {
            run.PlanRevision++;
            run.CurrentStepIndex = 0;
            return StageOutcome.To(AgentStage.Plan, $"Replanning as revision {run.PlanRevision} ({budget.ReplansLeft} replans left). Cause: {cause}");
        }

        return await EscalateAsync(run, $"Recovery budget exhausted (replans {budget.ReplansLeft}, retries {budget.RetriesLeft}). Last failure: {cause}", ct);
    }

    private async Task<StageOutcome> EscalateAsync(AgentRun run, string reason, CancellationToken ct)
    {
        var escalation = new AIEscalation
        {
            AgentRunId = run.Id,
            UserId = run.UserId,
            CourseId = run.CourseId,
            LearnerQuestion = run.TaskSpec.Length > 4000 ? run.TaskSpec[..4000] : run.TaskSpec,
            EscalationReason = $"Agent run {run.Id} ({run.TaskType}) could not recover: {reason}",
            Priority = "High",
            Status = "New",
        };
        _db.AIEscalations.Add(escalation);

        await _evidence.RecordAsync(run, new AgentTrace
        {
            Stage = Stage,
            Kind = AgentTrace.KindEscalation,
            Payload = JsonSerializer.Serialize(new { reason, priority = escalation.Priority }),
        }, ct);

        return StageOutcome.To(AgentStage.Failed, $"Escalated to instructor: {reason}");
    }
}
