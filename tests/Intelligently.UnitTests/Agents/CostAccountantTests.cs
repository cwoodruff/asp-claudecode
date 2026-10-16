using FluentAssertions;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Agents.Evidence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Intelligently.UnitTests.Agents;

/// <summary>Cost arithmetic for every token class, the price table lookup, and what a charge does to the run.</summary>
public class CostAccountantTests
{
    private static CostAccountant Accountant(Action<AgentsOptions>? configure = null)
    {
        var options = new AgentsOptions();
        configure?.Invoke(options);
        return new CostAccountant(Options.Create(options), NullLogger<CostAccountant>.Instance);
    }

    [Fact]
    public void Each_token_class_is_priced_at_its_own_rate()
    {
        var accountant = Accountant();

        // Opus 5.5 defaults: $4 in, $20 out, $0.20 cache read, $5 cache creation per million.
        accountant.CostOf(new ModelUsage("claude-opus-5-5", 1_000_000, 0)).Should().Be(4m);
        accountant.CostOf(new ModelUsage("claude-opus-5-5", 0, 1_000_000)).Should().Be(20m);
        accountant.CostOf(new ModelUsage("claude-opus-5-5", 0, 0, CacheReadInputTokens: 1_000_000)).Should().Be(0.20m);
        accountant.CostOf(new ModelUsage("claude-opus-5-5", 0, 0, CacheCreationInputTokens: 1_000_000)).Should().Be(5m);

        // Mixed usage adds up, rounded to six decimals.
        accountant.CostOf(new ModelUsage("claude-opus-5-5", 120, 40, 1000, 500))
            .Should().Be(0.00048m + 0.0008m + 0.0002m + 0.0025m);
        accountant.CostOf(new ModelUsage("claude-opus-5-5", 1, 0)).Should().Be(0.000004m);
        accountant.CostOf(new ModelUsage("claude-opus-5-5", 0, 0)).Should().Be(0m);
    }

    [Fact]
    public void The_cheap_model_has_its_own_row()
    {
        var accountant = Accountant();

        accountant.CostOf(new ModelUsage("claude-haiku-5-5", 1_000_000, 1_000_000, 1_000_000, 1_000_000))
            .Should().Be(0.10m + 0.50m + 0.01m + 0.125m);
    }

    [Fact]
    public void Configured_prices_replace_the_defaults_and_a_dated_snapshot_matches_by_prefix()
    {
        var accountant = Accountant(o => o.Pricing.Models["claude-opus-5-5"] = new AgentsOptions.ModelPricing
        {
            InputPerMillion = 10, OutputPerMillion = 50, CacheReadPerMillion = 1, CacheCreationPerMillion = 12.5m,
        });

        accountant.CostOf(new ModelUsage("claude-opus-5-5", 100_000, 10_000, 200_000, 40_000))
            .Should().Be(1m + 0.5m + 0.2m + 0.5m);
        accountant.CostOf(new ModelUsage("claude-opus-5-5-20260401", 1_000_000, 0)).Should().Be(10m, "a dated id shares its base model's rates");
    }

    [Fact]
    public void An_unpriced_model_costs_nothing_rather_than_failing_the_run()
    {
        var accountant = Accountant();

        accountant.CostOf(new ModelUsage("claude-unknown-9", 1_000_000, 1_000_000)).Should().Be(0m);
        accountant.CostOf(new ModelUsage("", 10, 10)).Should().Be(0m);
    }

    [Fact]
    public void Charging_a_call_adds_tokens_cost_and_spends_the_token_budget()
    {
        var accountant = Accountant();
        var run = new AgentRun { Budget = new RecoveryBudget { TokensLeft = 10_000 } };

        var cost = accountant.Charge(run, new ModelUsage("claude-opus-5-5", 120, 40, 1000, 500));
        accountant.Charge(run, new ModelUsage("claude-haiku-5-5", 2_000, 100));

        cost.Should().Be(0.00398m);
        run.TotalInputTokens.Should().Be(120 + 1000 + 500 + 2000, "cached reads and cache creation are part of the processed prompt");
        run.TotalOutputTokens.Should().Be(140);
        run.Budget.TokensLeft.Should().Be(10_000 - 1660 - 2100);
        run.TotalCostUsd.Should().Be(0.00398m + 0.00025m);
    }
}
