using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;

namespace Intelligently.Core.Agents.Contracts;

public enum PolicyVerdict { Allow, Deny, RequireApproval }

/// <summary>
/// Control Plane decision for one proposed tool call. <see cref="RuleKind"/> and <see cref="Rule"/>
/// name the rule that decided. An Allow may carry a <see cref="ReplayedResult"/> (the idempotency
/// barrier found an identical earlier call: return it, do not execute) or an
/// <see cref="IdempotencyKey"/> (execute, then record the result under that key).
/// </summary>
public sealed record PolicyDecision(
    PolicyVerdict Verdict,
    string? Reason = null,
    PolicyRuleKind? RuleKind = null,
    string? Rule = null,
    ToolResult? ReplayedResult = null,
    string? IdempotencyKey = null)
{
    public static readonly PolicyDecision Allowed = new(PolicyVerdict.Allow);
    public static PolicyDecision Allow(string reason, PolicyRuleKind kind, string? rule = null) => new(PolicyVerdict.Allow, reason, kind, rule);
    public static PolicyDecision Deny(string reason, PolicyRuleKind? kind = null, string? rule = null) => new(PolicyVerdict.Deny, reason, kind, rule);
    public static PolicyDecision RequireApproval(string reason, PolicyRuleKind? kind = null, string? rule = null) => new(PolicyVerdict.RequireApproval, reason, kind, rule);

    public bool IsReplay => ReplayedResult is not null;
}

/// <summary>Everything the Control Plane needs to judge one call.</summary>
public sealed record PolicyRequest(AgentRun Run, ToolCall Call, ToolDescriptor Descriptor, RunPrincipal Principal);

/// <summary>Control Plane: decides whether a proposed tool call may proceed.</summary>
public interface IPolicyDecisionPoint
{
    Task<PolicyDecision> EvaluateAsync(PolicyRequest request, CancellationToken ct = default);
}
