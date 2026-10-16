using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Infrastructure.Data;
using Intelligently.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Tools;

/// <summary>
/// The account commands, all through ASP.NET Core Identity's <see cref="UserManager{TUser}"/> and
/// <see cref="RoleManager{TRole}"/> as registered by <c>AddIntelligentlyIdentity</c>, so password rules,
/// normalisation and hashing are the hosts'. Enrolment is the <see cref="IntelligentlyClaims.CourseClaimType"/>
/// user claim and instructors are the <see cref="RunPrincipal.InstructorRole"/> role. Each method returns
/// the process exit code: 0 on success (including "already so"), 1 on failure.
/// </summary>
public sealed class AccountCommands(
    UserManager<IdentityUser> users,
    RoleManager<IdentityRole> roles,
    IntelligentlyDbContext db,
    IPasswordSource passwords,
    TextWriter output,
    TextWriter error)
{
    public async Task<int> CreateUserAsync(string userName, string email, bool instructor, IReadOnlyList<int> courses, CancellationToken ct = default)
    {
        if (!new EmailAddressAttribute().IsValid(email))
        {
            error.WriteLine($"'{email}' is not a valid email address.");
            return 1;
        }

        if (await users.FindByNameAsync(userName) is not null)
        {
            error.WriteLine($"A user named '{userName}' already exists.");
            return 1;
        }

        var password = passwords.ReadNewPassword(userName, error);
        if (password is null) return 1;

        // One transaction: a user is created with all of its role and claims, or not at all.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var user = new IdentityUser { UserName = userName, Email = email };
        if (!Succeeded(await users.CreateAsync(user, password), $"Could not create '{userName}'")) return 1;

        if (instructor)
        {
            if (!await EnsureInstructorRoleAsync()) return 1;
            if (!Succeeded(await users.AddToRoleAsync(user, RunPrincipal.InstructorRole), $"Could not make '{userName}' an instructor")) return 1;
        }

        if (courses.Count > 0
            && !Succeeded(await users.AddClaimsAsync(user, courses.Select(CourseClaim)), $"Could not enrol '{userName}'")) return 1;

        await transaction.CommitAsync(ct);

        output.WriteLine($"Created user '{userName}' (id {user.Id}).");
        output.WriteLine($"  Roles:   {(instructor ? RunPrincipal.InstructorRole : "none")}");
        output.WriteLine($"  Courses: {(courses.Count > 0 ? string.Join(", ", courses.Order()) : "none")}");
        return 0;
    }

    public async Task<int> GrantCourseAsync(string userName, IReadOnlyList<int> courses)
    {
        var user = await FindAsync(userName);
        if (user is null) return 1;

        var enrolled = await EnrolledCourseValuesAsync(user);
        var toAdd = courses.Where(c => !enrolled.Contains(c.ToString(CultureInfo.InvariantCulture))).ToList();

        if (toAdd.Count > 0
            && !Succeeded(await users.AddClaimsAsync(user, toAdd.Select(CourseClaim)), $"Could not enrol '{userName}'")) return 1;

        foreach (var course in courses)
            output.WriteLine(toAdd.Contains(course)
                ? $"Enrolled '{userName}' in course {course}."
                : $"'{userName}' is already enrolled in course {course}.");
        return 0;
    }

    public async Task<int> RevokeCourseAsync(string userName, IReadOnlyList<int> courses)
    {
        var user = await FindAsync(userName);
        if (user is null) return 1;

        var claims = await users.GetClaimsAsync(user);
        foreach (var course in courses)
        {
            var value = course.ToString(CultureInfo.InvariantCulture);
            // Every matching claim, so a duplicate row cannot leave the user enrolled.
            var matching = claims.Where(c => c.Type == IntelligentlyClaims.CourseClaimType && c.Value == value).ToList();
            if (matching.Count == 0)
            {
                output.WriteLine($"'{userName}' is not enrolled in course {course}.");
                continue;
            }

            if (!Succeeded(await users.RemoveClaimsAsync(user, matching), $"Could not remove '{userName}' from course {course}")) return 1;
            output.WriteLine($"Removed '{userName}' from course {course}.");
        }
        return 0;
    }

    public async Task<int> AddInstructorAsync(string userName)
    {
        var user = await FindAsync(userName);
        if (user is null) return 1;

        if (!await EnsureInstructorRoleAsync()) return 1;
        if (await users.IsInRoleAsync(user, RunPrincipal.InstructorRole))
        {
            output.WriteLine($"'{userName}' is already an instructor.");
            return 0;
        }

        if (!Succeeded(await users.AddToRoleAsync(user, RunPrincipal.InstructorRole), $"Could not make '{userName}' an instructor")) return 1;
        output.WriteLine($"'{userName}' is now an instructor.");
        return 0;
    }

    public async Task<int> RemoveInstructorAsync(string userName)
    {
        var user = await FindAsync(userName);
        if (user is null) return 1;

        if (!await roles.RoleExistsAsync(RunPrincipal.InstructorRole) || !await users.IsInRoleAsync(user, RunPrincipal.InstructorRole))
        {
            output.WriteLine($"'{userName}' is not an instructor.");
            return 0;
        }

        if (!Succeeded(await users.RemoveFromRoleAsync(user, RunPrincipal.InstructorRole), $"Could not remove the instructor role from '{userName}'")) return 1;
        output.WriteLine($"'{userName}' is no longer an instructor.");
        return 0;
    }

    /// <summary>Sets a new password and clears any lockout. The security stamp changes, so the user's existing sign-ins end.</summary>
    public async Task<int> ResetPasswordAsync(string userName, CancellationToken ct = default)
    {
        var user = await FindAsync(userName);
        if (user is null) return 1;

        var password = passwords.ReadNewPassword(userName, error);
        if (password is null) return 1;

        // Removing the old password and adding the new one commit together, so a password the
        // validators reject leaves the old one in place.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        if (await users.HasPasswordAsync(user)
            && !Succeeded(await users.RemovePasswordAsync(user), $"Could not reset the password of '{userName}'")) return 1;
        if (!Succeeded(await users.AddPasswordAsync(user, password), $"Could not reset the password of '{userName}'")) return 1;
        if (!Succeeded(await users.SetLockoutEndDateAsync(user, null), $"Could not clear the lockout of '{userName}'")) return 1;
        if (!Succeeded(await users.ResetAccessFailedCountAsync(user), $"Could not clear the lockout of '{userName}'")) return 1;

        await transaction.CommitAsync(ct);
        output.WriteLine($"Password reset for '{userName}'; lockout cleared.");
        return 0;
    }

    /// <summary>User name, email, roles, enrolled courses and lockout. Never password hashes or stamps.</summary>
    public async Task<int> ListUsersAsync(CancellationToken ct = default)
    {
        var all = await users.Users.OrderBy(u => u.UserName).ToListAsync(ct);
        if (all.Count == 0)
        {
            output.WriteLine("No users.");
            return 0;
        }

        var rows = new List<string[]> { new[] { "UserName", "Email", "Roles", "Courses", "LockedOutUntil" } };
        foreach (var user in all)
        {
            var userRoles = (await users.GetRolesAsync(user)).Order(StringComparer.OrdinalIgnoreCase);
            var courses = await EnrolledCourseValuesAsync(user);
            var lockout = user.LockoutEnd is { } end && end > DateTimeOffset.UtcNow
                ? end.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
                : "-";

            rows.Add(new[]
            {
                user.UserName ?? "",
                user.Email ?? "-",
                Joined(userRoles),
                Joined(courses.OrderBy(c => int.TryParse(c, out var id) ? id : int.MaxValue).ThenBy(c => c, StringComparer.Ordinal)),
                lockout,
            });
        }

        var widths = Enumerable.Range(0, rows[0].Length).Select(i => rows.Max(r => r[i].Length)).ToArray();
        foreach (var row in rows)
            output.WriteLine(string.Join("  ", row.Select((cell, i) => cell.PadRight(widths[i]))).TrimEnd());
        return 0;
    }

    private static Claim CourseClaim(int courseId) =>
        new(IntelligentlyClaims.CourseClaimType, courseId.ToString(CultureInfo.InvariantCulture));

    private static string Joined(IEnumerable<string> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? "-" : string.Join(",", list);
    }

    private async Task<HashSet<string>> EnrolledCourseValuesAsync(IdentityUser user) =>
        (await users.GetClaimsAsync(user))
            .Where(c => c.Type == IntelligentlyClaims.CourseClaimType)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);

    private async Task<IdentityUser?> FindAsync(string userName)
    {
        var user = await users.FindByNameAsync(userName);
        if (user is null) error.WriteLine($"No user named '{userName}'.");
        return user;
    }

    private async Task<bool> EnsureInstructorRoleAsync()
    {
        if (await roles.RoleExistsAsync(RunPrincipal.InstructorRole)) return true;
        var created = Succeeded(await roles.CreateAsync(new IdentityRole(RunPrincipal.InstructorRole)), $"Could not create the {RunPrincipal.InstructorRole} role");
        if (created) output.WriteLine($"Created the {RunPrincipal.InstructorRole} role.");
        return created;
    }

    private bool Succeeded(IdentityResult result, string what)
    {
        if (result.Succeeded) return true;
        error.WriteLine($"{what}:");
        foreach (var e in result.Errors) error.WriteLine($"  {e.Description}");
        return false;
    }
}
