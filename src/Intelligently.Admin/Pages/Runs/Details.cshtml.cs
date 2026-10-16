using Intelligently.Core.Agents.Entities;
using Intelligently.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Admin.Pages.Runs;

/// <summary>/runs/{id}: read-only trace viewer showing the run, its steps and its evidence rows in order.</summary>
public sealed class DetailsModel : PageModel
{
    private readonly IntelligentlyDbContext _db;

    public DetailsModel(IntelligentlyDbContext db)
    {
        _db = db;
    }

    public AgentRun Run { get; private set; } = null!;
    public IReadOnlyList<AgentPlan> Plans { get; private set; } = [];
    public IReadOnlyList<AgentStep> Steps { get; private set; } = [];
    public IReadOnlyList<AgentTrace> Traces { get; private set; } = [];
    public IReadOnlyList<AgentApproval> Approvals { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var run = await _db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null) return NotFound();

        Run = run;
        Plans = await _db.AgentPlans.AsNoTracking().Where(p => p.RunId == id).OrderBy(p => p.Revision).ToListAsync(ct);
        Steps = await _db.AgentSteps.AsNoTracking().Where(s => s.RunId == id)
            .OrderBy(s => s.StartedAt).ThenBy(s => s.PlanRevision).ThenBy(s => s.PlanStepIndex).ThenBy(s => s.Attempt)
            .ToListAsync(ct);
        Traces = await _db.AgentTraces.AsNoTracking().Where(t => t.RunId == id).OrderBy(t => t.Id).ToListAsync(ct);
        Approvals = await _db.AgentApprovals.AsNoTracking().Where(a => a.RunId == id).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        return Page();
    }
}
