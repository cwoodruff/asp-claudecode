using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>
/// AwaitingApproval: executed once the approval gate has recorded a decision and set the run back
/// to Running. Approved resumes the same Act step; denied marks the parked step failed and sends the
/// run to Recover with <see cref="RecoveryReasons.PolicyDenied"/>, which must replan without that tool.
/// </summary>
public sealed class AwaitingApprovalHandler : IStageHandler
{
    private readonly IApprovalGate _approvals;
    private readonly IntelligentlyDbContext _db;

    public AwaitingApprovalHandler(IApprovalGate approvals, IntelligentlyDbContext db)
    {
        _approvals = approvals;
        _db = db;
    }

    public AgentStage Stage => AgentStage.AwaitingApproval;

    public async Task<StageOutcome> ExecuteAsync(AgentRun run, CancellationToken ct = default)
    {
        var approval = await _approvals.FindLatestAsync(run.Id, ct)
                       ?? throw new InvalidOperationException($"Run {run.Id} is at AwaitingApproval but has no approval row.");

        switch (approval.Status)
        {
            case AgentApproval.StatusApproved:
                return StageOutcome.To(AgentStage.Act,
                    $"Approval {approval.Id} for tool '{approval.ToolName}' granted by {approval.DecidedBy}; resuming step {run.CurrentStepIndex}.");

            case AgentApproval.StatusDenied:
                var note = string.IsNullOrWhiteSpace(approval.DecisionNote) ? string.Empty : $" ({approval.DecisionNote})";
                var step = await _db.AgentSteps.FirstOrDefaultAsync(s => s.Id == approval.StepId, ct);
                if (step is not null)
                {
                    step.Status = AgentStep.StatusFailed;
                    step.VerifierMessage = $"Approval denied by {approval.DecidedBy}{note}.";
                    step.CompletedAt = DateTime.UtcNow;
                }
                run.RecoveryReason = RecoveryReasons.PolicyDenied;
                return StageOutcome.To(AgentStage.Recover,
                    $"Approval {approval.Id} for tool '{approval.ToolName}' denied by {approval.DecidedBy}{note}. Replan without tool '{approval.ToolName}'.");

            default:
                throw new InvalidOperationException($"Run {run.Id} was stepped while approval {approval.Id} is still pending.");
        }
    }
}
