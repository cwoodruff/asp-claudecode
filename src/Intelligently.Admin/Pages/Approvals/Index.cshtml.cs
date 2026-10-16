using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelligently.Admin.Pages.Approvals;

/// <summary>
/// /approvals: the Control Plane approval queue. Lists pending <see cref="AgentApproval"/> rows
/// and records approve/deny decisions through the <see cref="IApprovalGate"/>, which resumes the run.
/// htmx posts swap the decided row in place; a plain post redirects back to the list.
/// Instructors only; the decision is recorded under the signed-in user's name.
/// </summary>
[Authorize(Policy = AgentAuthorization.InstructorPolicy)]
public sealed class IndexModel : PageModel
{
    private readonly IApprovalGate _gate;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(IApprovalGate gate, ILogger<IndexModel> logger)
    {
        _gate = gate;
        _logger = logger;
    }

    public IReadOnlyList<AgentApproval> Pending { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Pending = await _gate.ListPendingAsync(ct);
    }

    public Task<IActionResult> OnPostApproveAsync(Guid id, string? note, CancellationToken ct) => DecideAsync(id, approved: true, note, ct);

    public Task<IActionResult> OnPostDenyAsync(Guid id, string? note, CancellationToken ct) => DecideAsync(id, approved: false, note, ct);

    private async Task<IActionResult> DecideAsync(Guid id, bool approved, string? note, CancellationToken ct)
    {
        var decidedBy = User.GetDisplayName();
        if (decidedBy is null) return Challenge();

        AgentApproval approval;
        try
        {
            approval = await _gate.DecideAsync(id, approved, decidedBy, note, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Approval {ApprovalId} could not be decided", id);
            return StatusCode(StatusCodes.Status409Conflict, ex.Message);
        }

        if (Request.Headers.ContainsKey("HX-Request"))
            return Partial("_ApprovalRow", approval);

        return RedirectToPage();
    }
}
