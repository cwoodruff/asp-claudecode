using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Entities.AI;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Intelligently.Infrastructure.Agents.Control;

/// <summary>
/// Approval gate (plan section 1.4). <see cref="RequestAsync"/> is called from the Act stage and
/// only adds rows (the runner commits them with the AwaitingApproval transition).
/// <see cref="DecideAsync"/> is called from Admin: it records the decision as evidence, resolves the
/// instructor escalation, sets the parked run back to Running and re-enqueues it so a worker takes
/// the AwaitingApproval edge (approved → Act, denied → Recover).
/// </summary>
public sealed class ApprovalGate : IApprovalGate
{
    private readonly IntelligentlyDbContext _db;
    private readonly IEvidenceSink _evidence;
    private readonly IRunQueue _queue;
    private readonly ILogger<ApprovalGate> _logger;

    public ApprovalGate(IntelligentlyDbContext db, IEvidenceSink evidence, IRunQueue queue, ILogger<ApprovalGate> logger)
    {
        _db = db;
        _evidence = evidence;
        _queue = queue;
        _logger = logger;
    }

    public async Task<AgentApproval> RequestAsync(AgentRun run, AgentStep step, ToolCall call, string reason, CancellationToken ct = default, PolicyDecision? decision = null)
    {
        var arguments = CanonicalJson.Serialize(call.Input);
        var escalation = new AIEscalation
        {
            AgentRunId = run.Id,
            UserId = run.UserId,
            CourseId = run.CourseId,
            LearnerQuestion = run.TaskSpec.Length > 4000 ? run.TaskSpec[..4000] : run.TaskSpec,
            AIAttemptedResponse = $"Proposed tool call '{call.Name}' with arguments {arguments}",
            EscalationReason = $"Agent run {run.Id} ({run.TaskType}) needs approval for tool '{call.Name}': {reason}",
            Priority = "Medium",
            Status = "New",
        };
        _db.AIEscalations.Add(escalation);

        var approval = new AgentApproval
        {
            RunId = run.Id,
            StepId = step.Id,
            ToolName = call.Name,
            ToolCallJson = JsonSerializer.Serialize(call),
            ArgsHash = CanonicalJson.Sha256(call.Input),
            Reason = reason,
            RuleKind = decision?.RuleKind?.ToString(),
            Rule = decision?.Rule,
            Status = AgentApproval.StatusPending,
            Escalation = escalation,
        };
        _db.AgentApprovals.Add(approval);

        await _evidence.RecordAsync(run, new AgentTrace
        {
            Stage = run.Stage,
            StepId = step.Id,
            Kind = AgentTrace.KindApproval,
            Payload = JsonSerializer.Serialize(new
            {
                approvalId = approval.Id,
                status = approval.Status,
                tool = call.Name,
                arguments = call.Input,
                reason,
                ruleKind = approval.RuleKind,
                rule = approval.Rule,
            }),
        }, ct);

        _logger.LogInformation("Run {RunId}: approval {ApprovalId} requested for tool {Tool} ({Reason})", run.Id, approval.Id, call.Name, reason);
        return approval;
    }

    public Task<AgentApproval?> FindApprovedAsync(Guid runId, string toolName, string argsHash, CancellationToken ct = default) =>
        _db.AgentApprovals.AsNoTracking()
            .Where(a => a.RunId == runId && a.ToolName == toolName && a.ArgsHash == argsHash && a.Status == AgentApproval.StatusApproved)
            .OrderByDescending(a => a.DecidedAt)
            .FirstOrDefaultAsync(ct);

    public Task<AgentApproval?> FindLatestAsync(Guid runId, CancellationToken ct = default) =>
        _db.AgentApprovals.AsNoTracking()
            .Where(a => a.RunId == runId)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<AgentApproval>> ListPendingAsync(CancellationToken ct = default) =>
        await _db.AgentApprovals.AsNoTracking()
            .Include(a => a.Run)
            .Where(a => a.Status == AgentApproval.StatusPending)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

    public async Task<AgentApproval> DecideAsync(Guid approvalId, bool approved, string decidedBy, string? note = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(decidedBy))
            throw new ArgumentException("The decision must name who made it.", nameof(decidedBy));

        var approval = await _db.AgentApprovals.Include(a => a.Run).FirstOrDefaultAsync(a => a.Id == approvalId, ct)
                       ?? throw new KeyNotFoundException($"Approval {approvalId} was not found.");
        if (!approval.IsPending)
            throw new InvalidOperationException($"Approval {approvalId} was already {approval.Status}.");

        var verb = approved ? "approved" : "denied";
        approval.Status = approved ? AgentApproval.StatusApproved : AgentApproval.StatusDenied;
        approval.DecidedBy = decidedBy;
        approval.DecidedAt = DateTime.UtcNow;
        approval.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        var run = approval.Run;
        var resume = run.IsWaitingForApproval;
        if (resume)
        {
            run.Status = AgentRun.StatusRunning;
            run.StatusReason = $"Tool '{approval.ToolName}' {verb} by {decidedBy}; resuming.";
            run.ConcurrencyStamp = Guid.NewGuid();
        }

        if (approval.EscalationId is int escalationId &&
            await _db.AIEscalations.FirstOrDefaultAsync(e => e.Id == escalationId, ct) is { } escalation)
        {
            escalation.Status = "Resolved";
            escalation.InstructorUserId = decidedBy;
            escalation.InstructorResponse = approval.DecisionNote ?? $"Tool call {verb}.";
            escalation.ResolvedAt = approval.DecidedAt;
        }

        await _evidence.RecordAsync(run, new AgentTrace
        {
            Stage = AgentStage.AwaitingApproval,
            StepId = approval.StepId,
            Kind = AgentTrace.KindApproval,
            Payload = JsonSerializer.Serialize(new
            {
                approvalId = approval.Id,
                status = approval.Status,
                tool = approval.ToolName,
                decidedBy,
                decidedAt = approval.DecidedAt,
                note = approval.DecisionNote,
                resumed = resume,
            }),
        }, ct);

        await _db.SaveChangesAsync(ct);

        if (resume)
            await _queue.EnqueueAsync(run.Id, ct);
        else
            _logger.LogWarning("Approval {ApprovalId} {Verb} but run {RunId} is {Stage}/{Status}; not re-enqueued", approval.Id, verb, run.Id, run.Stage, run.Status);

        _logger.LogInformation("Run {RunId}: approval {ApprovalId} for tool {Tool} {Verb} by {DecidedBy}", run.Id, approval.Id, approval.ToolName, verb, decidedBy);
        return approval;
    }
}
