using Intelligently.Core.Entities.AI;

namespace Intelligently.Core.Agents.Entities;

/// <summary>A human decision requested by the Control Plane for one tool call.</summary>
public sealed class AgentApproval
{
    public const string StatusPending = "Pending";
    public const string StatusApproved = "Approved";
    public const string StatusDenied = "Denied";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Guid StepId { get; set; }
    public string ToolName { get; set; } = string.Empty;

    /// <summary>The full <c>ToolCall</c> as JSON (id, name, input).</summary>
    public string ToolCallJson { get; set; } = "{}";

    /// <summary>SHA-256 of the canonical argument JSON; matches the idempotency key for the same call.</summary>
    public string ArgsHash { get; set; } = string.Empty;

    /// <summary>Why the policy decision point asked for a human.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Pending | Approved | Denied.</summary>
    public string Status { get; set; } = StatusPending;

    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }

    /// <summary>The <c>AIEscalation</c> that notified instructors.</summary>
    public int? EscalationId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AgentRun Run { get; set; } = null!;
    public AIEscalation? Escalation { get; set; }

    public bool IsPending => Status == StatusPending;
}
