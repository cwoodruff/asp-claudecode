using System.Threading.RateLimiting;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Control;

/// <summary>
/// Tool-dispatch rate limiter built from the <see cref="PolicyRuleKind.RateLimit"/> rules: one
/// <see cref="PartitionedRateLimiter{TResource}"/> per rule, partitioned per run or per user with a
/// sliding window. Separate from the HTTP limiter in the Web host. Permits are consumed in rule
/// order and are not refunded when a later rule rejects, which keeps the limiter simple and strict.
/// </summary>
public sealed class ToolRateLimiter : IToolRateLimiter, IDisposable, IAsyncDisposable
{
    private readonly List<(PolicyRule Rule, PartitionedRateLimiter<string> Limiter)> _limiters = [];

    public ToolRateLimiter(IOptions<AgentsOptions> options)
        : this(options.Value.Policy.EffectiveRules) { }

    /// <summary>Builds a limiter from an explicit rule set (tests, or a future policy editor).</summary>
    public ToolRateLimiter(IEnumerable<PolicyRule> rules)
    {
        foreach (var rule in rules.Where(r => r.Enabled && r.Kind == PolicyRuleKind.RateLimit))
        {
            var permits = Math.Max(1, rule.PermitLimit);
            var windowSeconds = Math.Max(1, rule.WindowSeconds);
            var limiter = PartitionedRateLimiter.Create<string, string>(partitionKey =>
                RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = TimeSpan.FromSeconds(windowSeconds),
                    SegmentsPerWindow = Math.Clamp(windowSeconds, 1, 6),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
            _limiters.Add((rule, limiter));
        }
    }

    public async ValueTask<RateLimitOutcome> TryAcquireAsync(PolicyRequest request, CancellationToken ct = default)
    {
        foreach (var (rule, limiter) in _limiters)
        {
            if (!rule.Matches(request.Descriptor)) continue;

            var key = rule.Scope == RateLimitScope.User
                ? $"user:{request.Principal.UserId}"
                : $"run:{request.Run.Id}";

            using var lease = await limiter.AcquireAsync(key, 1, ct);
            if (lease.IsAcquired) continue;

            TimeSpan? retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : null;
            return new RateLimitOutcome(false, rule.Name, retryAfter);
        }

        return RateLimitOutcome.Granted;
    }

    public void Dispose()
    {
        foreach (var (_, limiter) in _limiters)
            limiter.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, limiter) in _limiters)
            await limiter.DisposeAsync();
    }
}
