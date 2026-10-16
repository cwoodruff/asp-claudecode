using System.Text.Json;
using Intelligently.Core.Agents.Entities;
using Intelligently.Infrastructure.Data;
using Intelligently.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Admin.Pages.Runs;

/// <summary>
/// /runs/{id}: read-only trace viewer. Shows the run, its plans and approvals, then every step with its
/// model call (prompt envelope, response, reasoning summary, stop reason, tokens, cost, latency), tool calls
/// with their payloads and sandbox logs, and finally every evidence row in order. Raw JSON payloads are
/// loaded on demand through the <c>Trace</c> handler (htmx), so the page stays light.
/// </summary>
[Authorize(Policy = AgentAuthorization.InstructorPolicy)]
public sealed class DetailsModel : PageModel
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

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
    public AgentEpisode? Episode { get; private set; }

    /// <summary>Model-call rows that belong to no step (planning and judgement calls), in order.</summary>
    public IReadOnlyList<AgentTrace> PlanCalls => Traces.Where(t => t.Kind == AgentTrace.KindModelCall && t.StepId is null).ToList();

    /// <summary>The evidence rows recorded for one step, in order.</summary>
    public StepEvidence EvidenceFor(AgentStep step) => new(step, Traces.Where(t => t.StepId == step.Id).OrderBy(t => t.Id).ToList());

    public decimal TotalTraceCost => Traces.Sum(t => t.CostUsd);

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
        Episode = await _db.AgentEpisodes.AsNoTracking().FirstOrDefaultAsync(e => e.RunId == id, ct);
        return Page();
    }

    /// <summary>htmx: the raw JSON payload of one evidence row of this run, pretty-printed.</summary>
    public async Task<IActionResult> OnGetTraceAsync(Guid id, long traceId, CancellationToken ct)
    {
        var trace = await _db.AgentTraces.AsNoTracking().FirstOrDefaultAsync(t => t.RunId == id && t.Id == traceId, ct);
        if (trace is null) return NotFound();
        return Partial("_TracePayload", trace);
    }

    public static string PrettyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, Pretty);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>A named field of a trace payload as text (strings verbatim, other values as JSON), or null.</summary>
    public static string? PayloadField(AgentTrace trace, params string[] path)
    {
        try
        {
            using var doc = JsonDocument.Parse(trace.Payload);
            var element = doc.RootElement;
            foreach (var name in path)
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
                    return null;
            }
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => JsonSerializer.Serialize(element, Pretty),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public sealed record StepEvidence(AgentStep Step, IReadOnlyList<AgentTrace> Traces)
    {
        public AgentTrace? ModelCall => Traces.FirstOrDefault(t => t.Kind == AgentTrace.KindModelCall);
        public IEnumerable<AgentTrace> ToolCalls => Traces.Where(t => t.Kind == AgentTrace.KindToolCall);
        public IEnumerable<AgentTrace> SandboxLogs => Traces.Where(t => t.Kind == AgentTrace.KindSandboxLog);
        public IEnumerable<AgentTrace> Approvals => Traces.Where(t => t.Kind == AgentTrace.KindApproval);
        public decimal CostUsd => Traces.Sum(t => t.CostUsd);
        public int InputTokens => Traces.Sum(t => t.InputTokens + t.CacheReadInputTokens + t.CacheCreationInputTokens);
        public int OutputTokens => Traces.Sum(t => t.OutputTokens);
    }
}
