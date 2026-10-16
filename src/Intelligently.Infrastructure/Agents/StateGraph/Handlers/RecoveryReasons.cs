namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>Values stored in <c>AgentRun.RecoveryReason</c> so Recover knows which edge to take.</summary>
public static class RecoveryReasons
{
    /// <summary>The planner produced no usable plan; replan.</summary>
    public const string PlanFailed = "PlanFailed";

    /// <summary>A step failed verification; retry the step, then replan.</summary>
    public const string StepFailed = "StepFailed";

    /// <summary>The Control Plane denied a call (policy or human); the denial is final, so replan without that tool.</summary>
    public const string PolicyDenied = "PolicyDenied";
}
