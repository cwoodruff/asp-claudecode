using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Evidence;

/// <summary>
/// Cost accounting (plan section 1.5): prices each token class of a model call from
/// <c>Agents:Pricing</c> and charges tokens and USD to the run. The provider reports cached reads and
/// cache creation separately from <c>input_tokens</c>, so all three are part of the processed prompt:
/// they count toward the run's input total and token budget, each at its own rate.
/// </summary>
public sealed class CostAccountant : ICostAccountant
{
    private const decimal Million = 1_000_000m;

    private readonly AgentsOptions.PricingOptions _pricing;
    private readonly ILogger<CostAccountant> _logger;
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

    public CostAccountant(IOptions<AgentsOptions> options, ILogger<CostAccountant> logger)
    {
        _pricing = options.Value.Pricing;
        _logger = logger;
    }

    public decimal CostOf(ModelUsage usage)
    {
        var price = _pricing.For(usage.Model);
        if (price is null)
        {
            lock (_warned)
            {
                if (_warned.Add(usage.Model ?? string.Empty))
                    _logger.LogWarning("No price configured under Agents:Pricing for model {Model}; its usage is charged at zero", usage.Model);
            }
            return 0m;
        }

        var cost =
            usage.InputTokens * price.InputPerMillion +
            usage.OutputTokens * price.OutputPerMillion +
            usage.CacheReadInputTokens * price.CacheReadPerMillion +
            usage.CacheCreationInputTokens * price.CacheCreationPerMillion;
        return decimal.Round(cost / Million, 6, MidpointRounding.AwayFromZero);
    }

    public decimal Charge(AgentRun run, ModelUsage usage)
    {
        var input = usage.InputTokens + usage.CacheReadInputTokens + usage.CacheCreationInputTokens;
        run.TotalInputTokens += input;
        run.TotalOutputTokens += usage.OutputTokens;
        run.Budget.TrySpendTokens(input + usage.OutputTokens);

        var cost = CostOf(usage);
        run.TotalCostUsd += cost;
        return cost;
    }
}
