using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Data;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>Plan: ask the Intelligence Plane for a schema-valid plan and persist it as a new revision.</summary>
public sealed class PlanHandler : IStageHandler
{
    private readonly IContextAssembler _context;
    private readonly IReasoningEngine _engine;
    private readonly IToolRegistry _tools;
    private readonly IEvidenceSink _evidence;
    private readonly ICostAccountant _costs;
    private readonly IntelligentlyDbContext _db;
    private readonly string _model;

    public PlanHandler(
        IContextAssembler context,
        IReasoningEngine engine,
        IToolRegistry tools,
        IEvidenceSink evidence,
        ICostAccountant costs,
        IntelligentlyDbContext db,
        IOptions<Core.Agents.AgentsOptions> options)
    {
        _context = context;
        _engine = engine;
        _tools = tools;
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
        using (_evidence.StartModelCall(run, Stage, _model, "high"))
        {
            result = await _engine.PlanAsync(envelope, ct);
            await UsageAccounting.RecordAsync(_costs, _evidence, run, Stage, envelope, result.Usage, result.RawResponse, result.ContentJson, result.Metadata, null, ct);
        }

        if (run.Budget.IsExhausted)
            return Exhausted(run, "after the planner call");

        if (!result.Succeeded)
            return Recover(run, result.FailureReason ?? "Planner produced no plan.");

        var plan = result.Plan!;
        if (Validate(plan) is { } problem)
            return Recover(run, problem);

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

        return StageOutcome.To(AgentStage.Act, $"Plan revision {stored.Revision} produced with {stored.Steps.Count} step(s).");
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

    private string? Validate(AgentPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.Goal)) return "Plan has no goal.";
        if (plan.Steps.Count == 0) return "Plan has no steps.";

        var knownTools = _tools.Descriptors.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (step.Index != i) return $"Plan step {i} has index {step.Index}; indices must be sequential from 0.";
            if (string.IsNullOrWhiteSpace(step.Description)) return $"Plan step {i} has no description.";
            if (step.Tool is not null && !knownTools.Contains(step.Tool)) return $"Plan step {i} names unknown tool '{step.Tool}'.";
        }

        return null;
    }
}
