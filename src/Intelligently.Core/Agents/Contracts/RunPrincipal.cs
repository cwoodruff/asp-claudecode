namespace Intelligently.Core.Agents.Contracts;

/// <summary>
/// Identity a run acts under (plan section 1.4): the learner's user id, their roles and the
/// courses they are enrolled in. Persisted as an owned type on <c>AgentRun</c> so every tool call
/// is evaluated against the identity that started the run, not whoever happens to resume it.
/// </summary>
public sealed record RunPrincipal
{
    public const string LearnerRole = "Learner";
    public const string InstructorRole = "Instructor";

    public string UserId { get; init; } = string.Empty;
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>Course ids the principal may act in.</summary>
    public IReadOnlyList<int> CourseScope { get; init; } = [];

    /// <summary>True when a user id is present; "no principal, no tool".</summary>
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(UserId);

    public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);

    public bool CanAccessCourse(int courseId) => CourseScope.Contains(courseId);

    /// <summary>A learner enrolled in exactly the course the run belongs to.</summary>
    public static RunPrincipal Learner(string userId, int courseId) => new()
    {
        UserId = userId,
        Roles = [LearnerRole],
        CourseScope = [courseId],
    };
}
