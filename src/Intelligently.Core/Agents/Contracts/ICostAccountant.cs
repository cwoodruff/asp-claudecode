using Intelligently.Core.Agents.Entities;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>
/// Evidence Plane cost accounting (plan section 1.5): converts a model call's usage into USD from the
/// configured price table (<c>Agents:Pricing</c>) and charges it, with the tokens, to the run.
/// </summary>
public interface ICostAccountant
{
    /// <summary>USD for one call's usage: input, output, cache-read and cache-creation tokens each at their own rate. Zero for a model without a price.</summary>
    decimal CostOf(ModelUsage usage);

    /// <summary>
    /// Charges a model call to the run: input + cache_read + cache_creation tokens and output tokens to the
    /// totals and the token budget, and the cost to <see cref="AgentRun.TotalCostUsd"/>. Returns the cost.
    /// </summary>
    decimal Charge(AgentRun run, ModelUsage usage);
}
