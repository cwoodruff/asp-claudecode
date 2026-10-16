namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>Values stored in <c>AgentRun.RecoveryReason</c> so Recover knows which edge to take.</summary>
public static class RecoveryReasons
{
    public const string PlanFailed = "PlanFailed";
    public const string StepFailed = "StepFailed";
}
