using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>
/// Control Plane approval gate (plan section 1.4). Parks a run that needs a human decision,
/// notifies instructors through the existing escalation path, and resumes the run when the
/// decision is recorded.
/// </summary>
public interface IApprovalGate
{
    /// <summary>
    /// Writes the pending <see cref="AgentApproval"/> and its <c>AIEscalation</c> for a call the
    /// policy decision point sent back as RequireApproval. Nothing is saved; the runner commits with the stage.
    /// </summary>
    Task<AgentApproval> RequestAsync(AgentRun run, AgentStep step, ToolCall call, string reason, CancellationToken ct = default, PolicyDecision? decision = null);

    /// <summary>An approved decision for this exact call (same run, tool and argument hash), or null.</summary>
    Task<AgentApproval?> FindApprovedAsync(Guid runId, string toolName, string argsHash, CancellationToken ct = default);

    /// <summary>The most recent approval row for the run, whatever its status.</summary>
    Task<AgentApproval?> FindLatestAsync(Guid runId, CancellationToken ct = default);

    Task<IReadOnlyList<AgentApproval>> ListPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Records the decision as evidence, resolves the escalation, flips the parked run back to
    /// Running and re-enqueues it so a worker takes the AwaitingApproval edge. Saves immediately.
    /// </summary>
    Task<AgentApproval> DecideAsync(Guid approvalId, bool approved, string decidedBy, string? note = null, CancellationToken ct = default);
}
