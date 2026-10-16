using System.Diagnostics;
using System.Text.Json;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Agents.StateGraph.Handlers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Intelligence;

/// <summary>
/// Test-time compute for planning (plan section 1.1, Phase 6): N planner calls on the same envelope run in
/// parallel; each candidate is validated exactly as the Plan stage would (<see cref="PlanValidator"/>), the
/// valid ones are scored by <see cref="IReasoningEngine.JudgeAsync"/> against the task's plan rubric, and
/// the highest-scoring valid candidate is kept. The planner calls are charged and recorded before any judge
/// call is made, and no judge runs on a spent hard budget. Every planner and judge call is a <c>ModelCall</c>
/// row (with the envelope that call actually received) under its own model-call span; every candidate is a
/// <c>Candidate</c> row with its validation outcome, score, rationale and whether it was selected, so the
/// cost of the search is visible per run.
/// </summary>
public sealed class CandidateSearch : ICandidateSearch
{
    private const string GenericRubric =
        "Score the candidate plan from 0 to 1: the steps reach the goal, each step names an available tool or is reasoning only, " +
        "the success criteria are concrete and checkable, and nothing is redundant. Set passed to true when the score is 0.6 or higher.";

    private readonly IReasoningEngine _engine;
    private readonly IEvidenceSink _evidence;
    private readonly ICostAccountant _costs;
    private readonly ITaskTypeRegistry _tasks;
    private readonly PlanValidator _validator;
    private readonly AgentsOptions _options;
    private readonly ILogger<CandidateSearch> _logger;

    public CandidateSearch(
        IReasoningEngine engine,
        IEvidenceSink evidence,
        ICostAccountant costs,
        ITaskTypeRegistry tasks,
        PlanValidator validator,
        IOptions<AgentsOptions> options,
        ILogger<CandidateSearch> logger)
    {
        _engine = engine;
        _evidence = evidence;
        _costs = costs;
        _tasks = tasks;
        _validator = validator;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabledFor(string? taskType) => _options.CandidateSearch.IsEnabledFor(taskType);

    /// <summary>A model call kept open (its span not yet ended) until its evidence row is recorded under it.</summary>
    private sealed record PendingCall<T>(T Result, PromptEnvelope Envelope, Activity? Span);

    public async Task<CandidateSearchResult> SearchAsync(AgentRun run, PromptEnvelope envelope, CancellationToken ct = default)
    {
        var count = Math.Max(1, _options.CandidateSearch.Candidates);
        var model = _options.DefaultModel;
        var rubric = _tasks.Find(run.TaskType)?.PlanJudgementRubric(run) ?? GenericRubric;

        // 1. N planner calls in parallel on the same envelope (sampling makes them differ).
        var plans = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => PlanAsync(run, envelope, model, ct)));

        // 2. Charge and record every planner call before any further model work; the evidence sink and the cost
        //    accountant share the scoped unit of work, so this is sequential, each row under its own span.
        var planCosts = new decimal[count];
        for (var i = 0; i < count; i++)
        {
            var plan = plans[i].Result;
            planCosts[i] = await RecordAsync(plans[i], run, plan.Usage, plan.RawResponse, plan.ContentJson, plan.Metadata, ct);
        }

        // 3. Validate every candidate exactly as the Plan stage would; only executable plans are worth judging.
        var problems = new string?[count];
        for (var i = 0; i < count; i++)
        {
            var plan = plans[i].Result;
            problems[i] = plan.Succeeded ? _validator.Validate(run, plan.Plan!) : plan.FailureReason ?? "Planner produced no plan.";
        }

        // 4. One judge call per valid candidate, in parallel, unless the planner batch spent the hard budget.
        var judgements = new PendingCall<JudgementResult>?[count];
        var judgeCosts = new decimal[count];
        var budgeted = !run.Budget.IsExhausted;
        if (budgeted)
        {
            var judged = await Task.WhenAll(Enumerable.Range(0, count).Select(i => problems[i] is null
                ? JudgeAsync(run, envelope, plans[i].Result.Plan!, rubric, model, ct)
                : Task.FromResult<PendingCall<JudgementResult>?>(null)));
            for (var i = 0; i < count; i++)
            {
                judgements[i] = judged[i];
                if (judged[i] is { } judgement)
                    judgeCosts[i] = await RecordAsync(judgement, run, judgement.Result.Usage, judgement.Result.RawResponse, null, judgement.Result.Metadata, ct);
            }
        }
        else
        {
            _logger.LogWarning("Run {RunId}: hard budget exhausted after the planner batch; candidates are not judged", run.Id);
        }

        // 5. Keep the best: the highest score among the valid, judged candidates (the earliest on a tie); when
        //    nothing was judged the first valid candidate; when none is valid, the first failure.
        var best = -1;
        for (var i = 0; i < count; i++)
        {
            if (problems[i] is not null) continue;
            if (best < 0 || (judgements[i]?.Result.Score ?? 0) > (judgements[best]?.Result.Score ?? 0)) best = i;
        }

        var candidates = new List<PlanCandidate>(count);
        for (var i = 0; i < count; i++)
        {
            var plan = plans[i].Result;
            var judgement = judgements[i]?.Result;
            var candidate = new PlanCandidate(i, plan, judgement, Selected: i == best);
            candidates.Add(candidate);
            await _evidence.RecordAsync(run, new AgentTrace
            {
                Stage = AgentStage.Plan,
                Kind = AgentTrace.KindCandidate,
                Model = plan.Usage.Model,
                Payload = JsonSerializer.Serialize(new
                {
                    candidate = i,
                    candidates = count,
                    selected = candidate.Selected,
                    succeeded = problems[i] is null,
                    failureReason = problems[i],
                    judged = judgement is not null,
                    score = judgement?.Score,
                    passed = judgement?.Passed,
                    rationale = judgement?.Rationale,
                    goal = plan.Plan?.Goal,
                    steps = plan.Plan?.Steps.Select(s => new { s.Index, s.Phase, s.Tool, s.Description, s.SuccessCriteria }),
                    planCostUsd = planCosts[i],
                    judgeCostUsd = judgeCosts[i],
                    totalCostUsd = planCosts[i] + judgeCosts[i],
                }),
            }, ct);
        }

        // The failure handed back for an all-invalid search names the first problem, so Recover sees why.
        var bestResult = best >= 0
            ? plans[best].Result
            : plans[0].Result with { Plan = null, FailureReason = problems[0] };
        _logger.LogInformation("Run {RunId}: candidate search kept plan {Best} of {Count} (score {Score:0.00}, search cost ${Cost:0.000000})",
            run.Id, best, count, best >= 0 ? judgements[best]?.Result.Score ?? 0 : 0, planCosts.Sum() + judgeCosts.Sum());

        return new CandidateSearchResult(bestResult, candidates);
    }

    /// <summary>Records the call's row with the call's own span current, then ends the span.</summary>
    private async Task<decimal> RecordAsync<T>(PendingCall<T> call, AgentRun run, ModelUsage usage, string rawResponse, string? contentJson, ModelCallMetadata? metadata, CancellationToken ct)
    {
        var previous = Activity.Current;
        try
        {
            if (call.Span is not null) Activity.Current = call.Span;
            return await UsageAccounting.RecordAsync(_costs, _evidence, run, AgentStage.Plan, call.Envelope, usage, rawResponse, contentJson, metadata, null, ct);
        }
        finally
        {
            Activity.Current = previous;
            call.Span?.Dispose();
        }
    }

    private async Task<PendingCall<PlanResult>> PlanAsync(AgentRun run, PromptEnvelope envelope, string model, CancellationToken ct)
    {
        var span = _evidence.StartModelCall(run, AgentStage.Plan, model, "high");
        span?.SetTag("agent.candidate_search", true);
        try
        {
            return new PendingCall<PlanResult>(await _engine.PlanAsync(envelope, ct), envelope, span);
        }
        catch
        {
            span?.Dispose();
            throw;
        }
    }

    private async Task<PendingCall<JudgementResult>?> JudgeAsync(AgentRun run, PromptEnvelope envelope, AgentPlan plan, string rubric, string model, CancellationToken ct)
    {
        // The candidate is appended as the assistant's turn and the Plan-stage instruction is replaced by a judging
        // one; this envelope, not the planning one, is what the judge's evidence row stores.
        var messages = envelope.Messages.Where(m => m.Section != PromptSection.Instruction).ToList();
        messages.Add(new PromptMessage(PromptMessage.AssistantRole, JsonSerializer.Serialize(plan)));
        messages.Add(new PromptMessage(PromptMessage.SystemRole, "The assistant message above is a candidate plan for the task. Judge it against the rubric; do not produce a plan.", PromptSection.Instruction));
        var judged = envelope with { Messages = messages, Compaction = false };

        var span = _evidence.StartModelCall(run, AgentStage.Plan, model, "high");
        span?.SetTag("agent.candidate_search", true);
        span?.SetTag("agent.judge", true);
        try
        {
            return new PendingCall<JudgementResult>(await _engine.JudgeAsync(judged, rubric, ct), judged, span);
        }
        catch
        {
            span?.Dispose();
            throw;
        }
    }
}
