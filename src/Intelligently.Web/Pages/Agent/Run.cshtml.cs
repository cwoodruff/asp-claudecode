using Intelligently.Core.Agents.Entities;
using Intelligently.Infrastructure.Agents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelligently.Web.Pages.Agent;

/// <summary>
/// GET /agent/runs/{id}: the full page for browsers, or just the status partial
/// when htmx polls (HX-Request header). Returns 404 for unknown runs.
/// </summary>
public sealed class RunModel : PageModel
{
    private readonly AgentRunService _runs;

    public RunModel(AgentRunService runs)
    {
        _runs = runs;
    }

    public AgentRun Run { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var run = await _runs.FindAsync(id, ct);
        if (run is null) return NotFound();

        Run = run;
        if (Request.Headers.ContainsKey("HX-Request"))
            return Partial("_RunStatus", run);

        return Page();
    }
}
