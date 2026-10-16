using Intelligently.Core.Agents.Tools;

namespace Intelligently.Core.Agents.Contracts;

public enum PolicyVerdict { Allow, Deny, RequireApproval }

/// <summary>Control Plane decision for one proposed tool call.</summary>
public sealed record PolicyDecision(PolicyVerdict Verdict, string? Reason = null)
{
    public static readonly PolicyDecision Allowed = new(PolicyVerdict.Allow);
    public static PolicyDecision Deny(string reason) => new(PolicyVerdict.Deny, reason);
    public static PolicyDecision RequireApproval(string reason) => new(PolicyVerdict.RequireApproval, reason);
}

/// <summary>Identity the run acts under.</summary>
public sealed record RunPrincipal(string UserId, int CourseId, IReadOnlyList<string> Roles);

/// <summary>Control Plane: decides whether a proposed tool call may proceed.</summary>
public interface IPolicyDecisionPoint
{
    Task<PolicyDecision> EvaluateAsync(ToolCall call, ToolDescriptor descriptor, RunPrincipal principal, CancellationToken ct = default);
}
