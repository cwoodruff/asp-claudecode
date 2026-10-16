using System.Text.Json;
using Intelligently.Infrastructure.Agents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelligently.Web.Pages.Agent;

/// <summary>Demo form that creates a run and swaps in the polling status partial.</summary>
public sealed class IndexModel : PageModel
{
    private readonly AgentRunService _runs;

    public IndexModel(AgentRunService runs)
    {
        _runs = runs;
    }

    [BindProperty] public string UserId { get; set; } = "demo-learner";
    [BindProperty] public int CourseId { get; set; } = 1;
    [BindProperty] public string TaskType { get; set; } = "LabErrorDiagnosis";
    [BindProperty] public string TaskSpec { get; set; } = """{ "question": "My build fails with CS0246. What does it mean?" }""";
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
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

        var run = await _runs.CreateAsync(UserId.Trim(), CourseId, TaskType.Trim(), TaskSpec, ct: ct);

        if (Request.Headers.ContainsKey("HX-Request"))
            return Partial("_RunStatus", run);

        return RedirectToPage("/Agent/Run", new { id = run.Id });
    }
}
