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
/// parallel, each schema-valid candidate is scored by <see cref="IReasoningEngine.JudgeAsync"/> against the
/// task's plan rubric, and the highest-scoring candidate is kept. Every planner and judge call is charged to
/// the run and recorded as a <c>ModelCall</c> row; every candidate is recorded as a <c>Candidate</c> row with
/// its score, rationale and whether it was selected, so the cost of the search is visible per run.
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
    private readonly AgentsOptions _options;
    private readonly ILogger<CandidateSearch> _logger;

    public CandidateSearch(
        IReasoningEngine engine,
        IEvidenceSink evidence,
        ICostAccountant costs,
        ITaskTypeRegistry tasks,
        IOptions<AgentsOptions> options,
        ILogger<CandidateSearch> logger)
    {
        _engine = engine;
        _evidence = evidence;
        _costs = costs;
        _tasks = tasks;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabledFor(string? taskType) => _options.CandidateSearch.IsEnabledFor(taskType);

    public async Task<CandidateSearchResult> SearchAsync(AgentRun run, PromptEnvelope envelope, CancellationToken ct = default)
    {
        var count = Math.Max(1, _options.CandidateSearch.Candidates);
        var model = _options.DefaultModel;
        var rubric = _tasks.Find(run.TaskType)?.PlanJudgementRubric(run) ?? GenericRubric;

        // 1. N planner calls in parallel on the same envelope (sampling makes them differ), spans per call.
        var plans = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => PlanAsync(run, envelope, model, ct)));

        // 2. One judge call per schema-valid candidate, in parallel.
        var judgements = await Task.WhenAll(plans.Select(plan => plan.Succeeded ? JudgeAsync(run, envelope, plan.Plan!, rubric, model, ct) : Task.FromResult<JudgementResult?>(null)));

        // 3. Charge and record every call, in order (the evidence sink and the cost accountant are not thread-safe on the scoped unit of work).
        var planCosts = new decimal[count];
        var judgeCosts = new decimal[count];
        for (var i = 0; i < count; i++)
        {
            var plan = plans[i];
            planCosts[i] = await UsageAccounting.RecordAsync(_costs, _evidence, run, AgentStage.Plan, envelope, plan.Usage, plan.RawResponse, plan.ContentJson, plan.Metadata, null, ct);
            if (judgements[i] is { } judgement)
                judgeCosts[i] = await UsageAccounting.RecordAsync(_costs, _evidence, run, AgentStage.Plan, envelope, judgement.Usage, judgement.RawResponse, null, judgement.Metadata, null, ct);
        }

        // 4. Keep the best: the highest score among the valid candidates (the earliest on a tie); when none is valid, the first failure.
        var best = -1;
        for (var i = 0; i < count; i++)
        {
            if (!plans[i].Succeeded) continue;
            if (best < 0 || (judgements[i]?.Score ?? 0) > (judgements[best]?.Score ?? 0)) best = i;
        }

        var candidates = new List<PlanCandidate>(count);
        for (var i = 0; i < count; i++)
        {
            var candidate = new PlanCandidate(i, plans[i], judgements[i], Selected: i == best);
            candidates.Add(candidate);
            await _evidence.RecordAsync(run, new AgentTrace
            {
                Stage = AgentStage.Plan,
                Kind = AgentTrace.KindCandidate,
                Model = plans[i].Usage.Model,
                Payload = JsonSerializer.Serialize(new
                {
                    candidate = i,
                    candidates = count,
                    selected = candidate.Selected,
                    succeeded = plans[i].Succeeded,
                    failureReason = plans[i].FailureReason,
                    score = judgements[i]?.Score,
                    passed = judgements[i]?.Passed,
                    rationale = judgements[i]?.Rationale,
                    goal = plans[i].Plan?.Goal,
                    steps = plans[i].Plan?.Steps.Select(s => new { s.Index, s.Phase, s.Tool, s.Description, s.SuccessCriteria }),
                    planCostUsd = planCosts[i],
                    judgeCostUsd = judgeCosts[i],
                    totalCostUsd = planCosts[i] + judgeCosts[i],
                }),
            }, ct);
        }

        var bestResult = best >= 0 ? plans[best] : plans[0];
        _logger.LogInformation("Run {RunId}: candidate search kept plan {Best} of {Count} (score {Score:0.00}, search cost ${Cost:0.000000})",
            run.Id, best, count, best >= 0 ? judgements[best]?.Score ?? 0 : 0, planCosts.Sum() + judgeCosts.Sum());

        return new CandidateSearchResult(bestResult, candidates);
    }

    private async Task<PlanResult> PlanAsync(AgentRun run, PromptEnvelope envelope, string model, CancellationToken ct)
    {
        using var span = _evidence.StartModelCall(run, AgentStage.Plan, model, "high");
        span?.SetTag("agent.candidate_search", true);
        return await _engine.PlanAsync(envelope, ct);
    }

    private async Task<JudgementResult?> JudgeAsync(AgentRun run, PromptEnvelope envelope, AgentPlan plan, string rubric, string model, CancellationToken ct)
    {
        // The candidate is appended as the assistant's turn and the Plan-stage instruction is replaced by a judging one.
        var messages = envelope.Messages.Where(m => m.Section != PromptSection.Instruction).ToList();
        messages.Add(new PromptMessage(PromptMessage.AssistantRole, JsonSerializer.Serialize(plan)));
        messages.Add(new PromptMessage(PromptMessage.SystemRole, "The assistant message above is a candidate plan for the task. Judge it against the rubric; do not produce a plan.", PromptSection.Instruction));
        var judged = envelope with { Messages = messages, Compaction = false };

        using var span = _evidence.StartModelCall(run, AgentStage.Plan, model, "high");
        span?.SetTag("agent.candidate_search", true);
        span?.SetTag("agent.judge", true);
        return await _engine.JudgeAsync(judged, rubric, ct);
    }
}
