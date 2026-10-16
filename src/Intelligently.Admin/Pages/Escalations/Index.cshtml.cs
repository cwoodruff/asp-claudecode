using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Entities.AI;
using Intelligently.Infrastructure.Data;
using Intelligently.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Admin.Pages.Escalations;

/// <summary>
/// /escalations: escalations raised by agent runs (Recover could not recover, or a tool asked for a human).
/// An instructor resolves one with a response and may mark the outcome reusable, which publishes it to the
/// knowledge base through <see cref="IEscalationResolver"/> and links the run's episode to the entry (the
/// Learn stage's instructor side). htmx posts swap the resolved row in place; a plain post redirects.
/// </summary>
[Authorize(Policy = AgentAuthorization.InstructorPolicy)]
public sealed class IndexModel : PageModel
{
    private const int PageSize = 100;

    private readonly IntelligentlyDbContext _db;
    private readonly IEscalationResolver _resolver;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(IntelligentlyDbContext db, IEscalationResolver resolver, ILogger<IndexModel> logger)
    {
        _db = db;
        _resolver = resolver;
        _logger = logger;
    }

    public IReadOnlyList<AIEscalation> Escalations { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Escalations = await _db.AIEscalations.AsNoTracking()
            .Where(e => e.AgentRunId != null)
            .OrderBy(e => e.Status == "Resolved" || e.Status == "Closed")
            .ThenByDescending(e => e.CreatedAt)
            .Take(PageSize)
            .ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostResolveAsync(int id, string response, bool addToKnowledgeBase, CancellationToken ct)
    {
        var instructor = User.GetDisplayName();
        if (instructor is null) return Challenge();
        if (string.IsNullOrWhiteSpace(response)) return BadRequest("A response is required.");

        EscalationResolution resolution;
        try
        {
            resolution = await _resolver.ResolveAsync(id, instructor, response, addToKnowledgeBase, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Escalation {EscalationId} could not be resolved", id);
            return StatusCode(StatusCodes.Status409Conflict, ex.Message);
        }

        if (Request.Headers.ContainsKey("HX-Request"))
            return Partial("_EscalationRow", resolution.Escalation);

        return RedirectToPage();
    }
}
