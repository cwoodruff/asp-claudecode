using System.Security.Claims;
using Intelligently.Core.Agents.Contracts;

namespace Intelligently.Infrastructure.Identity;

/// <summary>
/// Claim types and the one place a <see cref="RunPrincipal"/> is derived from an authenticated
/// <see cref="ClaimsPrincipal"/>. Both hosts use this; nothing in a request body or form ever
/// contributes to the principal a run acts under.
/// </summary>
public static class IntelligentlyClaims
{
    /// <summary>
    /// One claim per course the user is enrolled in, value = the course id. Until an enrolment entity
    /// exists these are user claims (<c>AspNetUserClaims</c>) that Identity copies onto the cookie
    /// principal, so <c>UserManager.AddClaimAsync(user, new Claim(CourseClaimType, "7"))</c> enrols a user.
    /// </summary>
    public const string CourseClaimType = "intelligently:course";

    /// <summary>The authenticated user's id (Identity's <c>NameIdentifier</c> claim), or null when anonymous.</summary>
    public static string? GetUserId(this ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>The user name for audit fields such as <c>AgentApproval.DecidedBy</c>; falls back to the user id.</summary>
    public static string? GetDisplayName(this ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        var name = user.Identity.Name;
        return string.IsNullOrWhiteSpace(name) ? user.GetUserId() : name;
    }

    /// <summary>Course ids from the user's <see cref="CourseClaimType"/> claims; non-numeric values are ignored.</summary>
    public static IReadOnlyList<int> GetEnrolledCourseIds(this ClaimsPrincipal user) =>
        user.FindAll(CourseClaimType)
            .Select(c => int.TryParse(c.Value, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

    /// <summary>
    /// The <see cref="RunPrincipal"/> for this caller: user id, role claims and enrolled courses, all from
    /// claims. Returns null when the caller is not authenticated or carries no user id.
    /// </summary>
    public static RunPrincipal? ToRunPrincipal(this ClaimsPrincipal user)
    {
        var userId = user.GetUserId();
        if (userId is null) return null;

        return new RunPrincipal
        {
            UserId = userId,
            Roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            CourseScope = user.GetEnrolledCourseIds(),
        };
    }
}
