using Intelligently.Core.Agents.Tools;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>Which step of the Control Plane evaluation a rule belongs to. Rules are evaluated in this order.</summary>
public enum PolicyRuleKind
{
    Identity = 0,
    /// <summary>A call that changes protected course material (a course-provided file of the task spec) needs a human.</summary>
    CourseMaterial = 1,
    Risk = 2,
    RateLimit = 3,
    Idempotency = 4,
}

/// <summary>What a matching <see cref="PolicyRuleKind.Risk"/> rule does.</summary>
public enum PolicyEffect
{
    Allow,
    /// <summary>Allow when the call stays inside the run's declared course scope, otherwise require approval.</summary>
    AllowInScope,
    Deny,
    RequireApproval,
}

/// <summary>Partition of a <see cref="PolicyRuleKind.RateLimit"/> rule.</summary>
public enum RateLimitScope
{
    Run,
    User,
}

/// <summary>
/// One data-driven policy rule, bound from <c>Agents:Policy:Rules</c>. A rule applies to a tool call
/// when the tool name matches <see cref="Tool"/> and the tool's risk level is in <see cref="RiskLevels"/>
/// (or that list is empty). Rules are evaluated identity → course material → risk → rate limit → idempotency,
/// and within a kind in configuration order.
/// </summary>
public sealed class PolicyRule
{
    public string Name { get; set; } = string.Empty;
    public PolicyRuleKind Kind { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Tool name pattern: an exact name, a <c>prefix*</c> glob, or <c>*</c> for every tool.</summary>
    public string Tool { get; set; } = "*";

    /// <summary>Risk levels the rule applies to; empty means every level.</summary>
    public List<ToolRiskLevel> RiskLevels { get; set; } = [];

    // Identity rules
    /// <summary>The principal must hold at least one of these roles; empty means no role requirement.</summary>
    public List<string> RequiredRoles { get; set; } = [];

    /// <summary>When true, a course-scoped tool call must target a course the principal is enrolled in.</summary>
    public bool RequireCourseScope { get; set; } = true;

    // Risk and course-material rules
    public PolicyEffect Effect { get; set; } = PolicyEffect.RequireApproval;

    // Rate limit rules
    public RateLimitScope Scope { get; set; } = RateLimitScope.Run;
    public int PermitLimit { get; set; } = 20;
    public int WindowSeconds { get; set; } = 60;

    public bool Matches(ToolDescriptor descriptor)
    {
        if (!Enabled) return false;
        if (RiskLevels.Count > 0 && !RiskLevels.Contains(descriptor.RiskLevel)) return false;
        return MatchesTool(descriptor.Name);
    }

    public bool MatchesTool(string toolName)
    {
        var pattern = string.IsNullOrWhiteSpace(Tool) ? "*" : Tool.Trim();
        if (pattern == "*") return true;
        if (pattern.EndsWith('*'))
            return toolName.StartsWith(pattern[..^1], StringComparison.Ordinal);
        return string.Equals(toolName, pattern, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule set from plan section 1.4, used when <c>Agents:Policy:Rules</c> is empty:
    /// a principal is required, a call that changes course-provided material needs approval, ReadOnly is
    /// allowed, Mutating is allowed inside the run's course scope, Destructive and External always need
    /// approval, 20 tool calls per run and 60 per user per minute, and every non-ReadOnly call goes through
    /// the idempotency barrier.
    /// </summary>
    public static IReadOnlyList<PolicyRule> Defaults { get; } =
    [
        new() { Name = "enrolment", Kind = PolicyRuleKind.Identity, RequireCourseScope = true },
        new() { Name = "course-material-approval", Kind = PolicyRuleKind.CourseMaterial, Effect = PolicyEffect.RequireApproval },
        new() { Name = "read-only", Kind = PolicyRuleKind.Risk, RiskLevels = [ToolRiskLevel.ReadOnly], Effect = PolicyEffect.Allow },
        new() { Name = "mutating-in-scope", Kind = PolicyRuleKind.Risk, RiskLevels = [ToolRiskLevel.Mutating], Effect = PolicyEffect.AllowInScope },
        new() { Name = "destructive-approval", Kind = PolicyRuleKind.Risk, RiskLevels = [ToolRiskLevel.Destructive], Effect = PolicyEffect.RequireApproval },
        new() { Name = "external-approval", Kind = PolicyRuleKind.Risk, RiskLevels = [ToolRiskLevel.External], Effect = PolicyEffect.RequireApproval },
        new() { Name = "per-run", Kind = PolicyRuleKind.RateLimit, Scope = RateLimitScope.Run, PermitLimit = 20, WindowSeconds = 60 },
        new() { Name = "per-user", Kind = PolicyRuleKind.RateLimit, Scope = RateLimitScope.User, PermitLimit = 60, WindowSeconds = 60 },
        new() { Name = "idempotent-mutations", Kind = PolicyRuleKind.Idempotency, RiskLevels = [ToolRiskLevel.Mutating, ToolRiskLevel.Destructive, ToolRiskLevel.External] },
    ];
}
