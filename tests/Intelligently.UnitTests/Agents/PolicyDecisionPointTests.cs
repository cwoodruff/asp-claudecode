using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Control;
using Intelligently.Infrastructure.Agents.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// The policy decision point evaluates identity, then risk, then rate limit, then idempotency,
/// stops at the first rule that objects, and maps each risk level to the outcome in plan section 1.4.
/// </summary>
public class PolicyDecisionPointTests
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new { type = "object" });
    private static readonly ToolDescriptor ReadOnlyTool = new("lookup_notes", "read", Schema, ToolRiskLevel.ReadOnly, "course_id");
    private static readonly ToolDescriptor MutatingTool = new("update_notes", "write", Schema, ToolRiskLevel.Mutating, "course_id");
    private static readonly ToolDescriptor UnscopedMutatingTool = new("notify_learner", "write", Schema, ToolRiskLevel.Mutating);
    private static readonly ToolDescriptor DestructiveTool = new("delete_notes", "destroy", Schema, ToolRiskLevel.Destructive, "course_id");
    private static readonly ToolDescriptor ExternalTool = new("post_webhook", "external", Schema, ToolRiskLevel.External);

    private readonly List<string> _log = [];
    private readonly Mock<IToolRateLimiter> _rateLimiter = new(MockBehavior.Strict);
    private readonly Mock<IIdempotencyBarrier> _barrier = new(MockBehavior.Strict);
    private readonly Mock<IApprovalGate> _approvals = new(MockBehavior.Strict);

    public PolicyDecisionPointTests()
    {
        _rateLimiter.Setup(r => r.TryAcquireAsync(It.IsAny<PolicyRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => _log.Add("rate"))
            .ReturnsAsync(RateLimitOutcome.Granted);
        _barrier.Setup(b => b.ComputeKey(It.IsAny<ToolCall>())).Returns("ARGS-HASH");
        _barrier.Setup(b => b.FindAsync(It.IsAny<Guid>(), It.IsAny<string>(), "ARGS-HASH", It.IsAny<CancellationToken>()))
            .Callback(() => _log.Add("idempotency"))
            .ReturnsAsync((ToolResult?)null);
        _approvals.Setup(a => a.FindApprovedAsync(It.IsAny<Guid>(), It.IsAny<string>(), "ARGS-HASH", It.IsAny<CancellationToken>()))
            .Callback(() => _log.Add("approval-lookup"))
            .ReturnsAsync((AgentApproval?)null);
    }

    private PolicyDecisionPoint Pdp(IEnumerable<PolicyRule>? rules = null)
    {
        var options = new AgentsOptions();
        if (rules is not null) options.Policy.Rules = rules.ToList();
        return new PolicyDecisionPoint(Options.Create(options), _rateLimiter.Object, _barrier.Object, _approvals.Object, new TaskTypeRegistry([new LabErrorDiagnosisContract()]), NullLogger<PolicyDecisionPoint>.Instance);
    }

    private static AgentRun Run(RunPrincipal? principal, int courseId = 7) => new()
    {
        UserId = principal?.UserId ?? string.Empty,
        CourseId = courseId,
        Principal = principal ?? new RunPrincipal(),
    };

    private static PolicyRequest Request(ToolDescriptor tool, RunPrincipal? principal, int? courseId = 7, int runCourseId = 7)
    {
        var run = Run(principal, runCourseId);
        var call = new ToolCall("toolu_1", tool.Name, JsonSerializer.SerializeToElement(new { course_id = courseId, text = "x" }));
        return new PolicyRequest(run, call, tool, run.Principal);
    }

    private static readonly RunPrincipal Learner = RunPrincipal.Learner("learner-1", 7);

    // ── Rule ordering ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Identity_is_checked_before_risk_rate_and_idempotency()
    {
        var decision = await Pdp().EvaluateAsync(Request(DestructiveTool, principal: null));

        decision.Verdict.Should().Be(PolicyVerdict.Deny);
        decision.RuleKind.Should().Be(PolicyRuleKind.Identity);
        decision.Reason.Should().Contain("No principal");
        _log.Should().BeEmpty("a missing principal short-circuits every later rule");
    }

    [Fact]
    public async Task Risk_gate_is_checked_before_rate_limit_and_idempotency()
    {
        var decision = await Pdp().EvaluateAsync(Request(DestructiveTool, Learner));

        decision.Verdict.Should().Be(PolicyVerdict.RequireApproval);
        decision.RuleKind.Should().Be(PolicyRuleKind.Risk);
        _log.Should().Equal("approval-lookup");
        _rateLimiter.Verify(r => r.TryAcquireAsync(It.IsAny<PolicyRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rate_limit_is_checked_before_idempotency()
    {
        _rateLimiter.Setup(r => r.TryAcquireAsync(It.IsAny<PolicyRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => _log.Add("rate"))
            .ReturnsAsync(new RateLimitOutcome(false, "per-run", TimeSpan.FromSeconds(30)));

        var decision = await Pdp().EvaluateAsync(Request(MutatingTool, Learner));

        decision.Verdict.Should().Be(PolicyVerdict.Deny);
        decision.RuleKind.Should().Be(PolicyRuleKind.RateLimit);
        decision.Rule.Should().Be("per-run");
        decision.Reason.Should().Contain("Retry after 30s");
        _log.Should().Equal("rate");
    }

    [Fact]
    public async Task Rules_are_evaluated_identity_then_risk_then_rate_then_idempotency()
    {
        var decision = await Pdp().EvaluateAsync(Request(MutatingTool, Learner));

        decision.Verdict.Should().Be(PolicyVerdict.Allow);
        decision.IdempotencyKey.Should().Be("ARGS-HASH");
        decision.IsReplay.Should().BeFalse();
        _log.Should().Equal("rate", "idempotency");
    }

    [Fact]
    public async Task Idempotent_replay_is_an_Allow_carrying_the_stored_result()
    {
        var stored = new ToolResult("toolu_old", MutatingTool.Name, false, "{\"stored\":true}");
        _barrier.Setup(b => b.FindAsync(It.IsAny<Guid>(), MutatingTool.Name, "ARGS-HASH", It.IsAny<CancellationToken>()))
            .Callback(() => _log.Add("idempotency"))
            .ReturnsAsync(stored);

        var decision = await Pdp().EvaluateAsync(Request(MutatingTool, Learner));

        decision.Verdict.Should().Be(PolicyVerdict.Allow);
        decision.RuleKind.Should().Be(PolicyRuleKind.Idempotency);
        decision.IsReplay.Should().BeTrue();
        decision.ReplayedResult!.ToolCallId.Should().Be("toolu_1", "the stored result is re-addressed to the new call id");
        decision.ReplayedResult.Content.Should().Be("{\"stored\":true}");
        decision.IdempotencyKey.Should().BeNull("a replay must not be recorded again");
    }

    [Fact]
    public async Task ReadOnly_tools_skip_the_idempotency_barrier()
    {
        var decision = await Pdp().EvaluateAsync(Request(ReadOnlyTool, Learner));

        decision.Verdict.Should().Be(PolicyVerdict.Allow);
        decision.IdempotencyKey.Should().BeNull();
        _log.Should().Equal("rate");
    }

    // ── Identity ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Course_scoped_call_outside_the_principals_enrolment_is_denied()
    {
        var decision = await Pdp().EvaluateAsync(Request(ReadOnlyTool, Learner, courseId: 99));

        decision.Verdict.Should().Be(PolicyVerdict.Deny);
        decision.RuleKind.Should().Be(PolicyRuleKind.Identity);
        decision.Rule.Should().Be("enrolment");
        decision.Reason.Should().Contain("course 99");
        _log.Should().BeEmpty();
    }

    [Fact]
    public async Task Course_scoped_call_with_a_null_course_is_not_an_enrolment_violation()
    {
        var decision = await Pdp().EvaluateAsync(Request(ReadOnlyTool, Learner, courseId: null));
        decision.Verdict.Should().Be(PolicyVerdict.Allow);
    }

    [Fact]
    public async Task Identity_rule_can_require_a_role_for_a_tool_pattern()
    {
        var rules = PolicyRule.Defaults.Concat([
            new PolicyRule { Name = "instructors-only", Kind = PolicyRuleKind.Identity, Tool = "delete_*", RequiredRoles = ["Instructor"] },
        ]);

        var learnerDecision = await Pdp(rules).EvaluateAsync(Request(DestructiveTool, Learner));
        learnerDecision.Verdict.Should().Be(PolicyVerdict.Deny);
        learnerDecision.Rule.Should().Be("instructors-only");
        learnerDecision.Reason.Should().Contain("Instructor");

        var instructor = new RunPrincipal { UserId = "instructor-1", Roles = ["Instructor"], CourseScope = [7] };
        var instructorDecision = await Pdp(rules).EvaluateAsync(Request(DestructiveTool, instructor));
        instructorDecision.Verdict.Should().Be(PolicyVerdict.RequireApproval, "the role rule passes and the destructive risk gate still applies");
    }

    // ── Risk gate: one outcome per ToolRiskLevel ─────────────────────────────────────────

    [Fact]
    public async Task ReadOnly_is_allowed()
    {
        var decision = await Pdp().EvaluateAsync(Request(ReadOnlyTool, Learner));
        decision.Verdict.Should().Be(PolicyVerdict.Allow);
        decision.Rule.Should().Be("read-only");
    }

    [Fact]
    public async Task Mutating_inside_the_runs_course_scope_is_allowed()
    {
        var decision = await Pdp().EvaluateAsync(Request(MutatingTool, Learner, courseId: 7, runCourseId: 7));
        decision.Verdict.Should().Be(PolicyVerdict.Allow);
        decision.Rule.Should().Be("mutating-in-scope");
        decision.Reason.Should().Contain("inside the run's course scope");
    }

    [Fact]
    public async Task Mutating_outside_the_runs_course_scope_requires_approval()
    {
        var enrolledInBoth = new RunPrincipal { UserId = "learner-1", Roles = ["Learner"], CourseScope = [7, 8] };

        var decision = await Pdp().EvaluateAsync(Request(MutatingTool, enrolledInBoth, courseId: 8, runCourseId: 7));

        decision.Verdict.Should().Be(PolicyVerdict.RequireApproval);
        decision.Rule.Should().Be("mutating-in-scope");
        decision.Reason.Should().Contain("outside the run's declared course scope");
    }

    [Fact]
    public async Task Mutating_tool_without_a_course_argument_is_inside_scope()
    {
        var decision = await Pdp().EvaluateAsync(Request(UnscopedMutatingTool, Learner));
        decision.Verdict.Should().Be(PolicyVerdict.Allow);
    }

    [Fact]
    public async Task Destructive_always_requires_approval()
    {
        var decision = await Pdp().EvaluateAsync(Request(DestructiveTool, Learner));
        decision.Verdict.Should().Be(PolicyVerdict.RequireApproval);
        decision.Rule.Should().Be("destructive-approval");
    }

    [Fact]
    public async Task External_always_requires_approval()
    {
        var decision = await Pdp().EvaluateAsync(Request(ExternalTool, Learner));
        decision.Verdict.Should().Be(PolicyVerdict.RequireApproval);
        decision.Rule.Should().Be("external-approval");
    }

    [Fact]
    public async Task A_recorded_approval_for_the_exact_call_satisfies_the_risk_gate()
    {
        _approvals.Setup(a => a.FindApprovedAsync(It.IsAny<Guid>(), DestructiveTool.Name, "ARGS-HASH", It.IsAny<CancellationToken>()))
            .Callback(() => _log.Add("approval-lookup"))
            .ReturnsAsync(new AgentApproval { Status = AgentApproval.StatusApproved, DecidedBy = "instructor-1", DecidedAt = DateTime.UtcNow });

        var decision = await Pdp().EvaluateAsync(Request(DestructiveTool, Learner));

        decision.Verdict.Should().Be(PolicyVerdict.Allow);
        decision.Reason.Should().Contain("Approved by instructor-1");
        _log.Should().Equal("approval-lookup", "rate", "idempotency");
    }

    [Fact]
    public async Task A_configured_Deny_rule_is_final()
    {
        var rules = new List<PolicyRule>
        {
            new() { Name = "no-webhooks", Kind = PolicyRuleKind.Risk, Tool = "post_*", Effect = PolicyEffect.Deny },
        }.Concat(PolicyRule.Defaults);

        var decision = await Pdp(rules).EvaluateAsync(Request(ExternalTool, Learner));

        decision.Verdict.Should().Be(PolicyVerdict.Deny);
        decision.RuleKind.Should().Be(PolicyRuleKind.Risk);
        decision.Rule.Should().Be("no-webhooks");
        _log.Should().BeEmpty();
    }

    [Fact]
    public async Task No_matching_risk_rule_fails_closed_to_RequireApproval()
    {
        var rules = new[] { new PolicyRule { Name = "only-readonly", Kind = PolicyRuleKind.Risk, RiskLevels = [ToolRiskLevel.ReadOnly], Effect = PolicyEffect.Allow } };

        var decision = await Pdp(rules).EvaluateAsync(Request(MutatingTool, Learner));

        decision.Verdict.Should().Be(PolicyVerdict.RequireApproval);
        decision.Reason.Should().Contain("failing closed");
    }

    [Fact]
    public async Task Disabled_rules_are_ignored()
    {
        var rules = PolicyRule.Defaults.Select(r => r.Name == "destructive-approval"
            ? new PolicyRule { Name = r.Name, Kind = r.Kind, RiskLevels = r.RiskLevels, Effect = r.Effect, Enabled = false }
            : r);

        var decision = await Pdp(rules).EvaluateAsync(Request(DestructiveTool, Learner));

        decision.Verdict.Should().Be(PolicyVerdict.RequireApproval);
        decision.Reason.Should().Contain("failing closed", "with the destructive rule disabled nothing matches");
    }
}
