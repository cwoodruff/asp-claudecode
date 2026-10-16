using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.StateGraph;

namespace Intelligently.Core.Agents.Entities;

/// <summary>Operational view of one agent run (plan section 2.1).</summary>
public sealed class AgentRun
{
    public const string StatusRunning = "Running";
    public const string StatusWaiting = "Waiting";
    public const string StatusCompleted = "Completed";
    public const string StatusFailed = "Failed";
    public const string StatusCancelled = "Cancelled";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public int CourseId { get; set; }

    /// <summary>Task class, e.g. <c>LabErrorDiagnosis</c>.</summary>
    public string TaskType { get; set; } = string.Empty;

    /// <summary>JSON task specification, validated at Interpret.</summary>
    public string TaskSpec { get; set; } = "{}";

    public AgentStage Stage { get; set; } = AgentStage.Interpret;

    /// <summary>Running | Waiting | Completed | Failed | Cancelled.</summary>
    public string Status { get; set; } = StatusRunning;

    /// <summary>Reason recorded for the latest transition (failure reason when Failed).</summary>
    public string? StatusReason { get; set; }

    public int PlanRevision { get; set; }

    /// <summary>Index of the plan step currently being executed (0-based).</summary>
    public int CurrentStepIndex { get; set; }

    /// <summary>Why the run entered Recover: <c>PlanFailed</c>, <c>StepFailed</c> or <c>PolicyDenied</c>.</summary>
    public string? RecoveryReason { get; set; }

    public RecoveryBudget Budget { get; set; } = new();

    /// <summary>Identity the run acts under; every tool call is judged against it (plan section 1.4).</summary>
    public RunPrincipal Principal { get; set; } = new();

    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public decimal TotalCostUsd { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    /// <summary>Optimistic concurrency token, regenerated on every commit so a second worker instance cannot double-process a run.</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();

    public ICollection<AgentStep> Steps { get; set; } = [];
    public ICollection<AgentPlan> Plans { get; set; } = [];
    public ICollection<AgentTrace> Traces { get; set; } = [];
    public ICollection<AgentApproval> Approvals { get; set; } = [];

    public bool IsActive => Status == StatusRunning;

    /// <summary>Parked at <see cref="AgentStage.AwaitingApproval"/> waiting for a human decision.</summary>
    public bool IsWaitingForApproval => Stage == AgentStage.AwaitingApproval && Status == StatusWaiting;
}
