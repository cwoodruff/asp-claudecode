using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>
/// Act: one tool-use turn for the current plan step, dispatched through the Execution Plane.
/// When the Control Plane asks for approval the step is parked (status AwaitingApproval) with the
/// proposed calls; after approval the same step resumes with those calls and no new model turn.
/// </summary>
public sealed class ActHandler : IStageHandler
{
    private readonly IContextAssembler _context;
    private readonly IReasoningEngine _engine;
    private readonly IToolDispatcher _dispatcher;
    private readonly IApprovalGate _approvals;
    private readonly IEvidenceSink _evidence;
    private readonly ICostAccountant _costs;
    private readonly IntelligentlyDbContext _db;
    private readonly string _model;

    public ActHandler(
        IContextAssembler context,
        IReasoningEngine engine,
        IToolDispatcher dispatcher,
        IApprovalGate approvals,
        IEvidenceSink evidence,
        ICostAccountant costs,
        IntelligentlyDbContext db,
        IOptions<Core.Agents.AgentsOptions> options)
    {
        _context = context;
        _engine = engine;
        _dispatcher = dispatcher;
        _approvals = approvals;
        _evidence = evidence;
        _costs = costs;
        _db = db;
        _model = options.Value.DefaultModel;
    }

    public AgentStage Stage => AgentStage.Act;

    public async Task<StageOutcome> ExecuteAsync(AgentRun run, CancellationToken ct = default)
    {
        var parked = await _db.AgentSteps
            .Where(s => s.RunId == run.Id && s.PlanRevision == run.PlanRevision && s.PlanStepIndex == run.CurrentStepIndex
                        && s.Status == AgentStep.StatusAwaitingApproval)
            .OrderByDescending(s => s.Attempt)
            .FirstOrDefaultAsync(ct);
        if (parked is not null)
            return await ResumeAsync(run, parked, ct);

        var attempt = 1 + await _db.AgentSteps.CountAsync(
            s => s.RunId == run.Id && s.PlanRevision == run.PlanRevision && s.PlanStepIndex == run.CurrentStepIndex, ct);

        var step = new AgentStep
        {
            RunId = run.Id,
            PlanRevision = run.PlanRevision,
            PlanStepIndex = run.CurrentStepIndex,
            Attempt = attempt,
            Stage = Stage,
            Status = AgentStep.StatusRunning,
        };
        _db.AgentSteps.Add(step);

        var envelope = await _context.BuildAsync(run, Stage, ct);
        step.PromptTokens = envelope.InputTokens;

        DecisionResult decision;
        using (_evidence.StartModelCall(run, Stage, _model, "medium"))
        {
            decision = await _engine.DecideAsync(envelope, ct);
            await UsageAccounting.RecordAsync(_costs, _evidence, run, Stage, envelope, decision.Usage, decision.RawResponse, decision.ContentJson, decision.Metadata, step.Id, ct);
        }

        step.AssistantText = decision.AssistantText;
        step.ToolCallsJson = JsonSerializer.Serialize(decision.ToolCalls);
        step.ResponseContentJson = decision.ContentJson;

        if (decision.ToolCalls.Count == 0)
        {
            step.ToolResultsJson = "[]";
            return StageOutcome.To(AgentStage.Observe, "No tool calls; assistant answered directly.");
        }

        var affordable = new List<ToolCall>();
        var results = new List<ToolResult>();
        foreach (var call in decision.ToolCalls)
        {
            if (run.Budget.TrySpendToolCall()) affordable.Add(call);
            else results.Add(ToolResult.BudgetExhausted(call));
        }

        return await DispatchAsync(run, step, affordable, results, ct);
    }

    /// <summary>Approved: dispatch the parked calls again. The Control Plane now sees the recorded approval.</summary>
    private async Task<StageOutcome> ResumeAsync(AgentRun run, AgentStep step, CancellationToken ct)
    {
        var calls = JsonSerializer.Deserialize<List<ToolCall>>(step.ToolCallsJson ?? "[]") ?? [];
        var results = StepQueries.Results(step).ToList();
        var done = results.Select(r => r.ToolCallId).ToHashSet(StringComparer.Ordinal);
        var pending = calls.Where(c => !done.Contains(c.Id)).ToList();

        step.Status = AgentStep.StatusRunning;
        return await DispatchAsync(run, step, pending, results, ct, resumed: true);
    }

    private async Task<StageOutcome> DispatchAsync(AgentRun run, AgentStep step, IReadOnlyList<ToolCall> calls, List<ToolResult> results, CancellationToken ct, bool resumed = false)
    {
        if (calls.Count > 0)
        {
            var dispatch = await _dispatcher.DispatchAsync(run, calls, ct, step.Id);
            if (dispatch.RequiresApproval)
                return await ParkAsync(run, step, dispatch, results, ct);

            results.AddRange(dispatch.Results);
        }

        step.ToolResultsJson = JsonSerializer.Serialize(results);
        var prefix = resumed ? $"Resumed step {step.PlanStepIndex} after approval; " : string.Empty;
        return StageOutcome.To(AgentStage.Observe, $"{prefix}{results.Count} tool call(s) dispatched.");
    }

    private async Task<StageOutcome> ParkAsync(AgentRun run, AgentStep step, DispatchResult dispatch, List<ToolResult> results, CancellationToken ct)
    {
        var call = dispatch.PendingCall ?? throw new InvalidOperationException("Dispatcher asked for approval without naming the call.");
        var reason = dispatch.ApprovalReason ?? "A tool call requires approval.";

        step.Status = AgentStep.StatusAwaitingApproval;
        step.ToolResultsJson = JsonSerializer.Serialize(results);

        var approval = await _approvals.RequestAsync(run, step, call, reason, ct, dispatch.Decision);
        return StageOutcome.To(AgentStage.AwaitingApproval, $"Tool '{call.Name}' requires approval ({approval.Id}): {reason}");
    }
}
