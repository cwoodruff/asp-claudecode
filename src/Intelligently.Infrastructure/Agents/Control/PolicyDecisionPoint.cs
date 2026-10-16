using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Control;

/// <summary>
/// Rule-driven policy decision point (plan section 1.4). Rules come from <c>Agents:Policy:Rules</c>
/// (or <see cref="PolicyRule.Defaults"/>) and are evaluated in a fixed order:
/// <list type="number">
///   <item>Identity: no principal, no tool; role requirements; a course-scoped call must target a course the principal is enrolled in.</item>
///   <item>Course material: a call that changes protected course material (the task contract decides what that is, for example a
///   course-provided file of a lab) needs a human unless the exact call was already approved.</item>
///   <item>Risk gate: the first matching risk rule decides. ReadOnly → Allow, Mutating → Allow inside the run's course scope,
///   Destructive and External → RequireApproval. An explicit, recorded approval for the exact call satisfies the gate.</item>
///   <item>Rate limit: per-run and per-user permits for tool dispatch; exceeded → Deny.</item>
///   <item>Idempotency barrier: a covered call that already executed in this run is returned as a replay instead of running again.</item>
/// </list>
/// </summary>
public sealed class PolicyDecisionPoint : IPolicyDecisionPoint
{
    private readonly IReadOnlyList<PolicyRule> _rules;
    private readonly IToolRateLimiter _rateLimiter;
    private readonly IIdempotencyBarrier _barrier;
    private readonly IApprovalGate _approvals;
    private readonly ITaskTypeRegistry _tasks;
    private readonly ILogger<PolicyDecisionPoint> _logger;

    public PolicyDecisionPoint(
        IOptions<AgentsOptions> options,
        IToolRateLimiter rateLimiter,
        IIdempotencyBarrier barrier,
        IApprovalGate approvals,
        ITaskTypeRegistry tasks,
        ILogger<PolicyDecisionPoint> logger)
    {
        _rules = options.Value.Policy.EffectiveRules;
        _rateLimiter = rateLimiter;
        _barrier = barrier;
        _approvals = approvals;
        _tasks = tasks;
        _logger = logger;
    }

    public async Task<PolicyDecision> EvaluateAsync(PolicyRequest request, CancellationToken ct = default)
    {
        var decision = EvaluateIdentity(request)
                       ?? await EvaluateCourseMaterialAsync(request, ct)
                       ?? await EvaluateRiskAsync(request, ct);
        if (decision.Verdict != PolicyVerdict.Allow)
            return Log(request, decision);

        decision = await EvaluateRateLimitAsync(request, decision, ct);
        if (decision.Verdict != PolicyVerdict.Allow)
            return Log(request, decision);

        return Log(request, await EvaluateIdempotencyAsync(request, decision, ct));
    }

    // 1. Identity ---------------------------------------------------------------------------------

    private PolicyDecision? EvaluateIdentity(PolicyRequest request)
    {
        var principal = request.Principal;
        if (principal is null || !principal.IsAuthenticated)
            return PolicyDecision.Deny("No principal: the run carries no user id.", PolicyRuleKind.Identity);

        var descriptor = request.Descriptor;
        foreach (var rule in Matching(PolicyRuleKind.Identity, descriptor))
        {
            if (rule.RequiredRoles.Count > 0 && !rule.RequiredRoles.Any(principal.IsInRole))
                return PolicyDecision.Deny(
                    $"Rule '{rule.Name}': tool '{descriptor.Name}' requires one of the roles [{string.Join(", ", rule.RequiredRoles)}].",
                    PolicyRuleKind.Identity, rule.Name);

            if (rule.RequireCourseScope && descriptor.CourseIdOf(request.Call) is int courseId && !principal.CanAccessCourse(courseId))
                return PolicyDecision.Deny(
                    $"Rule '{rule.Name}': tool '{descriptor.Name}' targets course {courseId}, which is outside the principal's enrolment.",
                    PolicyRuleKind.Identity, rule.Name);
        }

        return null;
    }

    // 2. Course material --------------------------------------------------------------------------

    /// <summary>
    /// A call that changes protected course material (as the run's task contract defines it) is decided by the
    /// first matching course-material rule: Deny, or RequireApproval unless the exact call was already approved,
    /// in which case the risk gate decides as usual. Null when the call leaves course material intact.
    /// </summary>
    private async Task<PolicyDecision?> EvaluateCourseMaterialAsync(PolicyRequest request, CancellationToken ct)
    {
        var rule = Matching(PolicyRuleKind.CourseMaterial, request.Descriptor).FirstOrDefault();
        if (rule is null) return null;

        var change = _tasks.Find(request.Run.TaskType)?.DescribeProtectedChange(request.Run, request.Call);
        if (change is null) return null;

        var reason = $"Rule '{rule.Name}': {change}.";
        switch (rule.Effect)
        {
            case PolicyEffect.Allow:
            case PolicyEffect.AllowInScope:
                return null;
            case PolicyEffect.Deny:
                return PolicyDecision.Deny(reason, PolicyRuleKind.CourseMaterial, rule.Name);
            default:
                var approval = await _approvals.FindApprovedAsync(request.Run.Id, request.Call.Name, _barrier.ComputeKey(request.Call), ct);
                return approval is null
                    ? PolicyDecision.RequireApproval(reason, PolicyRuleKind.CourseMaterial, rule.Name)
                    : null;
        }
    }

    // 3. Risk gate --------------------------------------------------------------------------------

    private async Task<PolicyDecision> EvaluateRiskAsync(PolicyRequest request, CancellationToken ct)
    {
        var descriptor = request.Descriptor;
        var rule = Matching(PolicyRuleKind.Risk, descriptor).FirstOrDefault();
        var effect = rule?.Effect ?? PolicyEffect.RequireApproval;
        var ruleName = rule?.Name;
        var level = descriptor.RiskLevel;

        switch (effect)
        {
            case PolicyEffect.Allow:
                return PolicyDecision.Allow($"Rule '{ruleName}': {level} tool '{descriptor.Name}' is allowed.", PolicyRuleKind.Risk, ruleName);

            case PolicyEffect.Deny:
                return PolicyDecision.Deny($"Rule '{ruleName}': {level} tool '{descriptor.Name}' is denied.", PolicyRuleKind.Risk, ruleName);

            case PolicyEffect.AllowInScope when IsInsideRunScope(request):
                return PolicyDecision.Allow($"Rule '{ruleName}': {level} tool '{descriptor.Name}' stays inside the run's course scope ({request.Run.CourseId}).", PolicyRuleKind.Risk, ruleName);

            case PolicyEffect.AllowInScope:
                return await ApprovalOrRequireAsync(request,
                    $"Rule '{ruleName}': {level} tool '{descriptor.Name}' acts outside the run's declared course scope ({request.Run.CourseId}).", ruleName, ct);

            default:
                var reason = rule is null
                    ? $"No risk rule matches {level} tool '{descriptor.Name}'; failing closed."
                    : $"Rule '{ruleName}': {level} tool '{descriptor.Name}' always requires approval.";
                return await ApprovalOrRequireAsync(request, reason, ruleName, ct);
        }
    }

    private async Task<PolicyDecision> ApprovalOrRequireAsync(PolicyRequest request, string reason, string? ruleName, CancellationToken ct)
    {
        var approval = await _approvals.FindApprovedAsync(request.Run.Id, request.Call.Name, _barrier.ComputeKey(request.Call), ct);
        if (approval is not null)
            return PolicyDecision.Allow(
                $"Approved by {approval.DecidedBy} at {approval.DecidedAt:u} (approval {approval.Id}).",
                PolicyRuleKind.Risk, ruleName);

        return PolicyDecision.RequireApproval(reason, PolicyRuleKind.Risk, ruleName);
    }

    /// <summary>
    /// The run's declared scope is its course. A tool that is not course-scoped is inside it; a
    /// course-scoped call is inside it only when it names the run's course.
    /// </summary>
    private static bool IsInsideRunScope(PolicyRequest request)
    {
        if (request.Descriptor.CourseScopeArgument is null) return true;
        return request.Descriptor.CourseIdOf(request.Call) == request.Run.CourseId;
    }

    // 4. Rate limit -------------------------------------------------------------------------------

    private async Task<PolicyDecision> EvaluateRateLimitAsync(PolicyRequest request, PolicyDecision allowed, CancellationToken ct)
    {
        var outcome = await _rateLimiter.TryAcquireAsync(request, ct);
        if (outcome.Acquired) return allowed;

        var retry = outcome.RetryAfter is { } after ? $" Retry after {after.TotalSeconds:0}s." : string.Empty;
        return PolicyDecision.Deny(
            $"Rate limit '{outcome.Rule}' exceeded for tool '{request.Call.Name}'.{retry}",
            PolicyRuleKind.RateLimit, outcome.Rule);
    }

    // 5. Idempotency barrier ----------------------------------------------------------------------

    private async Task<PolicyDecision> EvaluateIdempotencyAsync(PolicyRequest request, PolicyDecision allowed, CancellationToken ct)
    {
        var rule = Matching(PolicyRuleKind.Idempotency, request.Descriptor).FirstOrDefault();
        if (rule is null) return allowed;

        var key = _barrier.ComputeKey(request.Call);
        var stored = await _barrier.FindAsync(request.Run.Id, request.Call.Name, key, ct);
        if (stored is not null)
        {
            return allowed with
            {
                Reason = $"Rule '{rule.Name}': identical call to '{request.Call.Name}' already executed in this run; returning the stored result.",
                RuleKind = PolicyRuleKind.Idempotency,
                Rule = rule.Name,
                ReplayedResult = stored with { ToolCallId = request.Call.Id, Name = request.Call.Name },
            };
        }

        return allowed with { IdempotencyKey = key };
    }

    private IEnumerable<PolicyRule> Matching(PolicyRuleKind kind, ToolDescriptor descriptor) =>
        _rules.Where(r => r.Kind == kind && r.Matches(descriptor));

    private PolicyDecision Log(PolicyRequest request, PolicyDecision decision)
    {
        _logger.LogDebug("Run {RunId}: {Verdict} {Tool} by {RuleKind}/{Rule}: {Reason}",
            request.Run.Id, decision.Verdict, request.Call.Name, decision.RuleKind, decision.Rule, decision.Reason);
        return decision;
    }
}
