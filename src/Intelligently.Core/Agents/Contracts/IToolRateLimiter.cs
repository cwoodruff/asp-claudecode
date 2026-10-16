namespace Intelligently.Core.Agents.Contracts;

/// <summary>Outcome of asking the tool-dispatch rate limiter for one permit.</summary>
public sealed record RateLimitOutcome(bool Acquired, string? Rule = null, TimeSpan? RetryAfter = null)
{
    public static readonly RateLimitOutcome Granted = new(true);
}

/// <summary>
/// Control Plane rate limiter for tool dispatch, partitioned per run and per user by the
/// <see cref="PolicyRuleKind.RateLimit"/> rules. Separate from the HTTP limiter in the Web host.
/// </summary>
public interface IToolRateLimiter
{
    ValueTask<RateLimitOutcome> TryAcquireAsync(PolicyRequest request, CancellationToken ct = default);
}
