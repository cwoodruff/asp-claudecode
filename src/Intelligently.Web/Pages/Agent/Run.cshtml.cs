using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Infrastructure.Agents;
using Intelligently.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelligently.Web.Pages.Agent;

/// <summary>
/// GET /agent/runs/{id}: the full page for browsers, or just the status partial
/// when htmx polls (HX-Request header). Only the learner who owns the run or an
/// instructor may see it; anyone else gets 403. Unknown runs are 404.
/// </summary>
[Authorize]
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
        var callerId = User.GetUserId();
        if (callerId is null) return Challenge();

        var run = await _runs.FindAsync(id, ct);
        if (run is null) return NotFound();

        var isOwner = string.Equals(run.UserId, callerId, StringComparison.Ordinal);
        if (!isOwner && !User.IsInRole(RunPrincipal.InstructorRole))
            return StatusCode(StatusCodes.Status403Forbidden);

        Run = run;
        if (Request.Headers.ContainsKey("HX-Request"))
            return Partial("_RunStatus", run);

        return Page();
    }
}
