using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>Act: one tool-use turn for the current plan step, dispatched through the Execution Plane.</summary>
public sealed class ActHandler : IStageHandler
{
    private readonly IContextAssembler _context;
    private readonly IReasoningEngine _engine;
    private readonly IToolDispatcher _dispatcher;
    private readonly IEvidenceSink _evidence;
    private readonly IntelligentlyDbContext _db;

    public ActHandler(IContextAssembler context, IReasoningEngine engine, IToolDispatcher dispatcher, IEvidenceSink evidence, IntelligentlyDbContext db)
    {
        _context = context;
        _engine = engine;
        _dispatcher = dispatcher;
        _evidence = evidence;
        _db = db;
    }

    public AgentStage Stage => AgentStage.Act;

    public async Task<StageOutcome> ExecuteAsync(AgentRun run, CancellationToken ct = default)
    {
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
        var decision = await _engine.DecideAsync(envelope, ct);

        UsageAccounting.Apply(run, decision.Usage);
        await _evidence.RecordAsync(run, UsageAccounting.ModelCallTrace(Stage, envelope, decision.Usage, decision.RawResponse, step.Id), ct);

        step.AssistantText = decision.AssistantText;
        step.ToolCallsJson = JsonSerializer.Serialize(decision.ToolCalls);

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
            else results.Add(ToolResult.Error(call, "Tool call budget exhausted."));
        }

        if (affordable.Count > 0)
        {
            var dispatch = await _dispatcher.DispatchAsync(run, affordable, ct);
            if (dispatch.RequiresApproval)
                return StageOutcome.To(AgentStage.AwaitingApproval, dispatch.ApprovalReason ?? "A tool call requires approval.");

            results.AddRange(dispatch.Results);
        }

        step.ToolResultsJson = JsonSerializer.Serialize(results);
        return StageOutcome.To(AgentStage.Observe, $"{results.Count} tool call(s) dispatched.");
    }
}
