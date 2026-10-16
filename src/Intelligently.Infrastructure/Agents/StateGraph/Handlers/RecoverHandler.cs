using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Entities.AI;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>
/// Recover (plan sections 2.2 and 2.3): spend exactly one recovery counter and take the matching
/// edge, or escalate to an instructor and fail.
/// <list type="bullet">
///   <item>Hard budget exhausted (tokens, tool calls, wall clock) → escalate.</item>
///   <item>Oscillation: the current plan's hash equals one of the previous three revisions, or the
///   current step failed twice with the same verifier message → escalate.</item>
///   <item>An instructor denied a change to course-provided material → escalate (the agent may not work around the denial).</item>
///   <item>Step failed with retries left → Act (retry the same step, RetriesLeft − 1).</item>
///   <item>Otherwise (plan failed, policy denied, retries spent) with replans left → Plan (PlanRevision + 1, ReplansLeft − 1).</item>
///   <item>Nothing left → escalate.</item>
/// </list>
/// </summary>
public sealed class RecoverHandler : IStageHandler
{
    /// <summary>How many earlier plan revisions are compared against the current plan's hash.</summary>
    public const int OscillationWindow = 3;

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

        if (await DetectOscillationAsync(run, ct) is { } oscillation)
            return await EscalateAsync(run, $"Oscillation detected: {oscillation}. Last failure: {cause}", ct);

        if (run.RecoveryReason == RecoveryReasons.MaterialDenied)
            return await EscalateAsync(run, $"An instructor denied the change to course-provided material; the lab cannot be fixed within the learner's files. Last failure: {cause}", ct);

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

    /// <summary>Returns a description of the oscillation, or null when the run is making progress.</summary>
    private async Task<string?> DetectOscillationAsync(AgentRun run, CancellationToken ct)
    {
        var oldest = run.PlanRevision - OscillationWindow;
        var plans = await _db.AgentPlans.AsNoTracking()
            .Where(p => p.RunId == run.Id && p.Revision <= run.PlanRevision && p.Revision >= oldest)
            .Select(p => new { p.Revision, p.Hash })
            .ToListAsync(ct);

        var current = plans.FirstOrDefault(p => p.Revision == run.PlanRevision);
        if (current is not null)
        {
            var repeat = plans.Where(p => p.Revision < run.PlanRevision && p.Hash == current.Hash)
                .OrderByDescending(p => p.Revision)
                .FirstOrDefault();
            if (repeat is not null)
                return $"plan revision {run.PlanRevision} is identical to revision {repeat.Revision}";
        }

        if (run.RecoveryReason == RecoveryReasons.StepFailed)
        {
            var failures = await _db.AgentSteps.AsNoTracking()
                .Where(s => s.RunId == run.Id && s.PlanRevision == run.PlanRevision && s.PlanStepIndex == run.CurrentStepIndex
                            && s.Status == AgentStep.StatusFailed)
                .OrderByDescending(s => s.Attempt)
                .Select(s => s.VerifierMessage)
                .Take(2)
                .ToListAsync(ct);

            if (failures.Count == 2 && failures[0] is not null && failures[0] == failures[1])
                return $"step {run.CurrentStepIndex} failed twice with the same verifier message \"{failures[0]}\"";
        }

        return null;
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
