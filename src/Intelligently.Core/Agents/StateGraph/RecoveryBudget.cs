namespace Intelligently.Core.Agents.StateGraph;

/// <summary>
/// Monotonic recovery counters (plan section 2.3). Values only ever decrease, so
/// every recovery edge spends something and the run is guaranteed to terminate.
/// Mapped as an owned type on <see cref="Entities.AgentRun"/>.
/// </summary>
public sealed class RecoveryBudget
{
    public int ReplansLeft { get; set; } = 3;
    public int RetriesLeft { get; set; } = 2;
    public int ToolCallsLeft { get; set; } = 40;
    public int TokensLeft { get; set; } = 300_000;
    public TimeSpan WallClockLeft { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Spends one replan; false when none are left.</summary>
    public bool TrySpendReplan()
    {
        if (ReplansLeft <= 0) return false;
        ReplansLeft--;
        return true;
    }

    /// <summary>Spends one retry; false when none are left.</summary>
    public bool TrySpendRetry()
    {
        if (RetriesLeft <= 0) return false;
        RetriesLeft--;
        return true;
    }

    /// <summary>Spends one tool call; false when none are left.</summary>
    public bool TrySpendToolCall()
    {
        if (ToolCallsLeft <= 0) return false;
        ToolCallsLeft--;
        return true;
    }

    /// <summary>Spends tokens (clamped at zero); false when the budget was already exhausted.</summary>
    public bool TrySpendTokens(int tokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);
        if (TokensLeft <= 0) return false;
        TokensLeft = Math.Max(0, TokensLeft - tokens);
        return true;
    }

    /// <summary>Spends wall-clock time (clamped at zero); false when the budget was already exhausted.</summary>
    public bool TrySpendWallClock(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (WallClockLeft <= TimeSpan.Zero) return false;
        var remaining = WallClockLeft - elapsed;
        WallClockLeft = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        return true;
    }

    /// <summary>True when any hard budget (tokens, tool calls, wall clock) is spent.</summary>
    public bool IsExhausted => TokensLeft <= 0 || ToolCallsLeft <= 0 || WallClockLeft <= TimeSpan.Zero;

    public RecoveryBudget Clone() => new()
    {
        ReplansLeft = ReplansLeft,
        RetriesLeft = RetriesLeft,
        ToolCallsLeft = ToolCallsLeft,
        TokensLeft = TokensLeft,
        WallClockLeft = WallClockLeft,
    };
}
