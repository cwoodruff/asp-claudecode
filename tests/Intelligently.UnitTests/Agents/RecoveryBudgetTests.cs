using FluentAssertions;
using Intelligently.Core.Agents.StateGraph;

namespace Intelligently.UnitTests.Agents;

public class RecoveryBudgetTests
{
    [Fact]
    public void Counters_only_decrease_and_stop_at_zero()
    {
        var budget = new RecoveryBudget { ReplansLeft = 1, RetriesLeft = 1, ToolCallsLeft = 1, TokensLeft = 10 };

        budget.TrySpendReplan().Should().BeTrue();
        budget.TrySpendReplan().Should().BeFalse();
        budget.ReplansLeft.Should().Be(0);

        budget.TrySpendRetry().Should().BeTrue();
        budget.TrySpendRetry().Should().BeFalse();

        budget.TrySpendToolCall().Should().BeTrue();
        budget.TrySpendToolCall().Should().BeFalse();

        budget.TrySpendTokens(25).Should().BeTrue();
        budget.TokensLeft.Should().Be(0);
        budget.TrySpendTokens(1).Should().BeFalse();
        budget.IsExhausted.Should().BeTrue();
    }

    [Fact]
    public void Wall_clock_clamps_at_zero()
    {
        var budget = new RecoveryBudget { WallClockLeft = TimeSpan.FromSeconds(1) };
        budget.TrySpendWallClock(TimeSpan.FromSeconds(5)).Should().BeTrue();
        budget.WallClockLeft.Should().Be(TimeSpan.Zero);
        budget.TrySpendWallClock(TimeSpan.FromSeconds(1)).Should().BeFalse();
    }
}
