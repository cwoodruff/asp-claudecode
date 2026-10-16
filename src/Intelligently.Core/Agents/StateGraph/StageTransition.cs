namespace Intelligently.Core.Agents.StateGraph;

/// <summary>A directed edge in the state graph.</summary>
public readonly record struct StageTransition(AgentStage From, AgentStage To);

/// <summary>
/// The transition table from the implementation plan (section 2.2). The runner
/// rejects any edge that is not listed here, so the graph cannot drift.
/// </summary>
public static class StageTransitions
{
    public static readonly IReadOnlySet<StageTransition> Allowed = new HashSet<StageTransition>
    {
        // Interpret
        new(AgentStage.Interpret, AgentStage.Plan),              // spec valid, within budget
        new(AgentStage.Interpret, AgentStage.Failed),            // spec invalid or out of scope
        // Plan
        new(AgentStage.Plan, AgentStage.Act),                    // plan produced and schema-valid
        new(AgentStage.Plan, AgentStage.Recover),                // planner cannot produce a plan
        // Act
        new(AgentStage.Act, AgentStage.Observe),                 // tool calls dispatched
        new(AgentStage.Act, AgentStage.AwaitingApproval),        // PDP returned RequireApproval
        // AwaitingApproval
        new(AgentStage.AwaitingApproval, AgentStage.Act),        // approved (resumes same step)
        new(AgentStage.AwaitingApproval, AgentStage.Recover),    // denied or timed out
        // Observe
        new(AgentStage.Observe, AgentStage.Verify),              // results captured
        // Verify
        new(AgentStage.Verify, AgentStage.Act),                  // step passes, plan has more steps
        new(AgentStage.Verify, AgentStage.Learn),                // step passes, plan complete
        new(AgentStage.Verify, AgentStage.Recover),              // step fails
        // Recover
        new(AgentStage.Recover, AgentStage.Plan),                // budget allows replan
        new(AgentStage.Recover, AgentStage.Act),                 // budget allows retry of same step
        new(AgentStage.Recover, AgentStage.Failed),              // budget exhausted → escalate → Failed
        // Learn
        new(AgentStage.Learn, AgentStage.Completed),             // episode and traces written
    };

    public static bool IsAllowed(AgentStage from, AgentStage to) => Allowed.Contains(new StageTransition(from, to));

    /// <summary>True for stages a run can be parked in that no handler executes.</summary>
    public static bool IsTerminal(AgentStage stage) => stage is AgentStage.Completed or AgentStage.Failed;
}
