using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>
/// Outcome of dispatching the tool calls of one assistant turn. When <see cref="RequiresApproval"/>
/// is set nothing was executed and <see cref="PendingCall"/> is the call that needs a human.
/// </summary>
public sealed record DispatchResult(
    IReadOnlyList<ToolResult> Results,
    bool RequiresApproval,
    string? ApprovalReason = null,
    ToolCall? PendingCall = null);

/// <summary>
/// Execution Plane entry point. Never runs a tool directly: asks the Control Plane
/// first, executes, then hands results to the Evidence Plane.
/// </summary>
public interface IToolDispatcher
{
    /// <summary>Dispatches the calls of one turn. <paramref name="stepId"/> is the step the calls belong to, recorded on their evidence rows.</summary>
    Task<DispatchResult> DispatchAsync(AgentRun run, IReadOnlyList<ToolCall> calls, CancellationToken ct = default, Guid? stepId = null);
}
