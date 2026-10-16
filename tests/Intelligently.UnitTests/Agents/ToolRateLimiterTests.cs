using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Control;

namespace Intelligently.UnitTests.Agents;

/// <summary>The tool-dispatch limiter partitions permits per run and per user from the rate-limit rules.</summary>
public class ToolRateLimiterTests
{
    private static readonly ToolDescriptor Tool = new("update_notes", "d", JsonSerializer.SerializeToElement(new { type = "object" }), ToolRiskLevel.Mutating);

    private static PolicyRequest Request(Guid runId, string userId)
    {
        var principal = RunPrincipal.Learner(userId, 7);
        var run = new AgentRun { Id = runId, UserId = userId, CourseId = 7, Principal = principal };
        return new PolicyRequest(run, new ToolCall("c", Tool.Name, JsonSerializer.SerializeToElement(new { })), Tool, principal);
    }

    [Fact]
    public async Task Per_run_permits_are_independent_between_runs()
    {
        await using var limiter = new ToolRateLimiter(
        [
            new PolicyRule { Name = "per-run", Kind = PolicyRuleKind.RateLimit, Scope = RateLimitScope.Run, PermitLimit = 2, WindowSeconds = 60 },
        ]);
        var runA = Request(Guid.NewGuid(), "u1");
        var runB = Request(Guid.NewGuid(), "u1");

        (await limiter.TryAcquireAsync(runA)).Acquired.Should().BeTrue();
        (await limiter.TryAcquireAsync(runA)).Acquired.Should().BeTrue();
        var third = await limiter.TryAcquireAsync(runA);
        third.Acquired.Should().BeFalse();
        third.Rule.Should().Be("per-run");

        (await limiter.TryAcquireAsync(runB)).Acquired.Should().BeTrue("another run has its own partition");
    }

    [Fact]
    public async Task Per_user_permits_are_shared_across_a_users_runs()
    {
        await using var limiter = new ToolRateLimiter(
        [
            new PolicyRule { Name = "per-run", Kind = PolicyRuleKind.RateLimit, Scope = RateLimitScope.Run, PermitLimit = 10, WindowSeconds = 60 },
            new PolicyRule { Name = "per-user", Kind = PolicyRuleKind.RateLimit, Scope = RateLimitScope.User, PermitLimit = 3, WindowSeconds = 60 },
        ]);
        var runA = Request(Guid.NewGuid(), "u1");
        var runB = Request(Guid.NewGuid(), "u1");
        var other = Request(Guid.NewGuid(), "u2");

        (await limiter.TryAcquireAsync(runA)).Acquired.Should().BeTrue();
        (await limiter.TryAcquireAsync(runA)).Acquired.Should().BeTrue();
        (await limiter.TryAcquireAsync(runB)).Acquired.Should().BeTrue();
        var fourth = await limiter.TryAcquireAsync(runB);
        fourth.Acquired.Should().BeFalse();
        fourth.Rule.Should().Be("per-user");

        (await limiter.TryAcquireAsync(other)).Acquired.Should().BeTrue("a different user has a different partition");
    }

    [Fact]
    public async Task Rules_that_do_not_match_the_tool_do_not_consume_permits()
    {
        await using var limiter = new ToolRateLimiter(
        [
            new PolicyRule { Name = "webhooks", Kind = PolicyRuleKind.RateLimit, Tool = "post_*", Scope = RateLimitScope.Run, PermitLimit = 1, WindowSeconds = 60 },
        ]);
        var request = Request(Guid.NewGuid(), "u1");

        for (var i = 0; i < 5; i++)
            (await limiter.TryAcquireAsync(request)).Acquired.Should().BeTrue();
    }

    [Fact]
    public async Task Without_rate_limit_rules_everything_is_granted()
    {
        await using var limiter = new ToolRateLimiter(PolicyRule.Defaults.Where(r => r.Kind != PolicyRuleKind.RateLimit));
        (await limiter.TryAcquireAsync(Request(Guid.NewGuid(), "u1"))).Should().Be(RateLimitOutcome.Granted);
    }
}
