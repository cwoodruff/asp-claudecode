using System.Text.Json;
using Intelligently.Infrastructure.Agents;
using Intelligently.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelligently.Web.Pages.Agent;

/// <summary>
/// Demo form that creates a run as the signed-in user and swaps in the polling status partial.
/// The form carries no user id; the principal is derived from the caller's claims.
/// </summary>
[Authorize]
public sealed class IndexModel : PageModel
{
    private readonly AgentRunService _runs;

    public IndexModel(AgentRunService runs)
    {
        _runs = runs;
    }

    [BindProperty] public int CourseId { get; set; } = 1;
    [BindProperty] public string TaskType { get; set; } = "LabErrorDiagnosis";
    [BindProperty] public string TaskSpec { get; set; } = """{ "question": "My build fails with CS0246. What does it mean?" }""";
    public string? Error { get; private set; }

    /// <summary>The caller's enrolled courses, shown so the demo picks a course the run may act in.</summary>
    public IReadOnlyList<int> EnrolledCourses { get; private set; } = [];

    public void OnGet()
    {
        EnrolledCourses = User.GetEnrolledCourseIds();
        if (EnrolledCourses.Count > 0 && !EnrolledCourses.Contains(CourseId))
            CourseId = EnrolledCourses[0];
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        EnrolledCourses = User.GetEnrolledCourseIds();
        var principal = User.ToRunPrincipal();
        if (principal is null) return Challenge();

        try
        {
            using var doc = JsonDocument.Parse(TaskSpec);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("The task spec must be a JSON object.");
        }
        catch (JsonException ex)
        {
            Error = $"Task spec is not valid JSON: {ex.Message}";
            return Page();
        }

        if (!principal.CanAccessCourse(CourseId))
        {
            Error = $"You are not enrolled in course {CourseId}.";
            return Page();
        }

        var run = await _runs.CreateAsync(principal.UserId, CourseId, TaskType.Trim(), TaskSpec, principal, ct);

        if (Request.Headers.ContainsKey("HX-Request"))
            return Partial("_RunStatus", run);

        return RedirectToPage("/Agent/Run", new { id = run.Id });
    }
}
