using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Agents.Intelligence;
using Intelligently.Infrastructure.Data;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>
/// Plan: ask the Intelligence Plane for a schema-valid plan and persist it as a new revision. When
/// candidate search is enabled for the task type, N plans are generated and judged and the best one is
/// kept (every candidate is evidence). <see cref="PlanValidator"/> applies the structural checks and the task
/// contract's template constraints to whichever plan comes back.
/// </summary>
public sealed class PlanHandler : IStageHandler
{
    private readonly IContextAssembler _context;
    private readonly IReasoningEngine _engine;
    private readonly ICandidateSearch _search;
    private readonly PlanValidator _validator;
    private readonly IEvidenceSink _evidence;
    private readonly ICostAccountant _costs;
    private readonly IntelligentlyDbContext _db;
    private readonly string _model;

    public PlanHandler(
        IContextAssembler context,
        IReasoningEngine engine,
        ICandidateSearch search,
        PlanValidator validator,
        IEvidenceSink evidence,
        ICostAccountant costs,
        IntelligentlyDbContext db,
        IOptions<Core.Agents.AgentsOptions> options)
    {
        _context = context;
        _engine = engine;
        _search = search;
        _validator = validator;
        _evidence = evidence;
        _costs = costs;
        _db = db;
        _model = options.Value.DefaultModel;
    }

    public AgentStage Stage => AgentStage.Plan;

    public async Task<StageOutcome> ExecuteAsync(AgentRun run, CancellationToken ct = default)
    {
        // A spent hard budget stops the run before any further model work; Recover escalates.
        if (run.Budget.IsExhausted)
            return Exhausted(run, "before planning");

        var envelope = await _context.BuildAsync(run, Stage, ct);

        PlanResult result;
        var searched = string.Empty;
        if (_search.IsEnabledFor(run.TaskType))
        {
            var search = await _search.SearchAsync(run, envelope, ct);
            result = search.Best;
            var kept = search.Candidates.FirstOrDefault(c => c.Selected);
            searched = kept is null
                ? $" Candidate search: none of {search.Candidates.Count} candidates was valid."
                : $" Candidate search kept candidate {kept.Index} of {search.Candidates.Count} (score {kept.Score:0.00}).";
        }
        else
        {
            using (_evidence.StartModelCall(run, Stage, _model, "high"))
            {
                result = await _engine.PlanAsync(envelope, ct);
                await UsageAccounting.RecordAsync(_costs, _evidence, run, Stage, envelope, result.Usage, result.RawResponse, result.ContentJson, result.Metadata, null, ct);
            }
        }

        if (run.Budget.IsExhausted)
            return Exhausted(run, "after the planner call");

        if (!result.Succeeded)
            return Recover(run, (result.FailureReason ?? "Planner produced no plan.") + searched);

        var plan = result.Plan!;
        if (_validator.Validate(run, plan) is { } problem)
            return Recover(run, problem + searched);

        var canonical = JsonSerializer.Serialize(plan);
        var stored = plan with
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            Revision = run.PlanRevision,
            Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
            CreatedAt = DateTime.UtcNow,
            ResponseContentJson = result.ContentJson,
        };
        _db.AgentPlans.Add(stored);
        run.CurrentStepIndex = 0;
        run.RecoveryReason = null;

        return StageOutcome.To(AgentStage.Act, $"Plan revision {stored.Revision} produced with {stored.Steps.Count} step(s).{searched}");
    }

    private static StageOutcome Recover(AgentRun run, string reason)
    {
        run.RecoveryReason = RecoveryReasons.PlanFailed;
        return StageOutcome.To(AgentStage.Recover, reason);
    }

    private static StageOutcome Exhausted(AgentRun run, string when)
    {
        var budget = run.Budget;
        run.RecoveryReason = RecoveryReasons.BudgetExhausted;
        return StageOutcome.To(AgentStage.Recover,
            $"Hard budget exhausted {when} (tokens {budget.TokensLeft}, tool calls {budget.ToolCallsLeft}, wall clock {budget.WallClockLeft}); no further model work.");
    }
}
