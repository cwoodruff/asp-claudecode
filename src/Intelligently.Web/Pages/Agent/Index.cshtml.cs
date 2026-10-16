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
    [BindProperty] public string TaskSpec { get; set; } = DefaultLabSpecFor(1);

    /// <summary>A LabErrorDiagnosis spec the demo form starts with: one learner file, the compiler error, no expected output, for the given course.</summary>
    public static string DefaultLabSpecFor(int courseId) => DefaultLabSpecTemplate.Replace("{course_id}", courseId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private const string DefaultLabSpecTemplate = """
        {
          "course_id": {course_id},
          "module_id": 3,
          "language": "csharp",
          "learner_files": [
            { "path": "Program.cs", "content": "using System;\nusing Newtonsoft.Json;\n\nConsole.WriteLine(JsonConvert.SerializeObject(new { ok = true }));\n" }
          ],
          "course_files": [],
          "entrypoint": "Program.cs",
          "error_text": "error CS0246: The type or namespace name 'Newtonsoft' could not be found",
          "expected_output": null,
          "learner_level": "Beginner"
        }
        """;
    public string? Error { get; private set; }

    /// <summary>The caller's enrolled courses, shown so the demo picks a course the run may act in.</summary>
    public IReadOnlyList<int> EnrolledCourses { get; private set; } = [];

    public void OnGet()
    {
        EnrolledCourses = User.GetEnrolledCourseIds();
        if (EnrolledCourses.Count > 0 && !EnrolledCourses.Contains(CourseId))
            CourseId = EnrolledCourses[0];
        // The default spec must name the course the run will act in; a spec the learner typed is left alone (POST binds it).
        TaskSpec = DefaultLabSpecFor(CourseId);
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
