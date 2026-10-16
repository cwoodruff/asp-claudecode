using Intelligently.Core.Agents.Entities;
using Intelligently.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Admin.Pages.Runs;

/// <summary>/runs: the most recent agent runs, as an entry point to the trace viewer.</summary>
public sealed class IndexModel : PageModel
{
    private const int PageSize = 50;
    private readonly IntelligentlyDbContext _db;

    public IndexModel(IntelligentlyDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<AgentRun> Runs { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Runs = await _db.AgentRuns.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(PageSize)
            .ToListAsync(ct);
    }
}
