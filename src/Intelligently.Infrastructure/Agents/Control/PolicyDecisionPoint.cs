using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Tools;

namespace Intelligently.Infrastructure.Agents.Control;

/// <summary>
/// Phase 1 policy: ReadOnly tools are allowed, everything else is denied.
/// Phase 2 replaces this with data-driven rules, rate limits and the approval gate.
/// </summary>
public sealed class PolicyDecisionPoint : IPolicyDecisionPoint
{
    public Task<PolicyDecision> EvaluateAsync(ToolCall call, ToolDescriptor descriptor, RunPrincipal principal, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(principal.UserId))
            return Task.FromResult(PolicyDecision.Deny("No principal: the run carries no user id."));

        var decision = descriptor.RiskLevel == ToolRiskLevel.ReadOnly
            ? PolicyDecision.Allowed
            : PolicyDecision.Deny($"Tool '{descriptor.Name}' is {descriptor.RiskLevel}; only ReadOnly tools are permitted in Phase 1.");

        return Task.FromResult(decision);
    }
}
