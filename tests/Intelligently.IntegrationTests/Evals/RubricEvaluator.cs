using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;

namespace Intelligently.IntegrationTests.Evals;

/// <summary>One rubric criterion's verdict for one fixture.</summary>
public sealed record CriterionResult(string Criterion, bool Passed, string Detail);

/// <summary>A fixture's score: the criteria evaluated and how many passed.</summary>
public sealed record FixtureScore(string Fixture, IReadOnlyList<CriterionResult> Criteria)
{
    public int Passed => Criteria.Count(c => c.Passed);
    public double PassRate => Criteria.Count == 0 ? 0 : (double)Passed / Criteria.Count;
    public IEnumerable<CriterionResult> Failures => Criteria.Where(c => !c.Passed);
}

/// <summary>
/// The evaluation rubric (plan section 1.5): deterministic checks over a stored trace that hold for every
/// well-formed run plus checks per outcome class. Each criterion is scored independently so a regression
/// shows up as a pass-rate drop with the failing criterion named, not as a single red test.
/// </summary>
public static class RubricEvaluator
{
    public static FixtureScore Evaluate(TraceFixture fixture)
    {
        var results = new List<CriterionResult>();
        void Check(string criterion, bool passed, string detail) => results.Add(new CriterionResult(criterion, passed, detail));

        // --- Structure: the state graph was honoured --------------------------------------------------
        var stages = fixture.Stages.Select(ParseStage).ToList();
        var illegal = stages.Zip(stages.Skip(1), (from, to) => (from, to)).Where(e => !StageTransitions.IsAllowed(e.from, e.to)).ToList();
        Check("transitions.legal", stages.Count >= 2 && illegal.Count == 0,
            illegal.Count == 0 ? $"{stages.Count - 1} transition(s), all in the table" : $"illegal: {string.Join(", ", illegal.Select(e => $"{e.from}→{e.to}"))}");
        Check("transitions.start_at_interpret", stages.FirstOrDefault() == AgentStage.Interpret, $"first stage {stages.FirstOrDefault()}");
        Check("transitions.end_terminal", stages.Count > 0 && StageTransitions.IsTerminal(stages[^1]), $"last stage {stages.LastOrDefault()}");
        Check("run.stage_matches_last_transition", stages.Count > 0 && ParseStage(fixture.Run.Stage) == stages[^1], $"run.stage {fixture.Run.Stage}");

        var transitions = fixture.Traces.Where(t => t.Kind == AgentTrace.KindTransition).ToList();
        Check("evidence.one_transition_row_per_executed_stage", transitions.Count == Math.Max(0, stages.Count - 1),
            $"{transitions.Count} transition row(s) for {Math.Max(0, stages.Count - 1)} executed stage(s)");
        Check("evidence.transition_rows_match_stages", transitions.Select(t => t.To).SequenceEqual(fixture.Stages.Skip(1)),
            "transition targets follow the stage sequence");

        // --- Evidence: every model call is accounted for ------------------------------------------------
        var modelCalls = fixture.Traces.Where(t => t.Kind == AgentTrace.KindModelCall).ToList();
        var modelStages = stages.Count(s => s is AgentStage.Plan or AgentStage.Act);
        Check("evidence.model_call_per_reasoning_stage", modelCalls.Count == modelStages, $"{modelCalls.Count} model call(s) for {modelStages} Plan/Act stage(s)");
        Check("evidence.model_calls_priced", modelCalls.All(t => t.CostUsd > 0 && t.Model is not null), "every model call has a model and a non-zero cost");
        Check("evidence.model_calls_have_stop_reason_and_effort", modelCalls.All(t => !string.IsNullOrEmpty(t.StopReason) && !string.IsNullOrEmpty(t.Effort)), "stop reason and effort recorded");
        Check("evidence.model_calls_have_latency", modelCalls.All(t => t.LatencyMs is > 0), "latency recorded");
        Check("evidence.reasoning_summary_present", modelCalls.Count == 0 || modelCalls.Any(t => !string.IsNullOrWhiteSpace(t.ReasoningSummary)), "at least one call returned a summarized reasoning block");

        var tokensIn = modelCalls.Sum(t => t.InputTokens + t.CacheReadInputTokens + t.CacheCreationInputTokens);
        var tokensOut = modelCalls.Sum(t => t.OutputTokens);
        Check("totals.tokens_match_rows", tokensIn == fixture.Run.TotalInputTokens && tokensOut == fixture.Run.TotalOutputTokens,
            $"rows {tokensIn}/{tokensOut} vs run {fixture.Run.TotalInputTokens}/{fixture.Run.TotalOutputTokens}");
        Check("totals.cost_matches_rows", modelCalls.Sum(t => t.CostUsd) == fixture.Run.TotalCostUsd,
            $"rows ${modelCalls.Sum(t => t.CostUsd)} vs run ${fixture.Run.TotalCostUsd}");
        Check("budget.cost_ceiling", fixture.Run.TotalCostUsd <= fixture.Expect.MaxCostUsd, $"${fixture.Run.TotalCostUsd} ≤ ${fixture.Expect.MaxCostUsd}");
        Check("budget.model_call_ceiling", modelCalls.Count <= fixture.Expect.MaxModelCalls, $"{modelCalls.Count} ≤ {fixture.Expect.MaxModelCalls}");
        Check("budget.tokens_spent_from_budget", fixture.Run.Budget.TokensLeft == 300_000 - (fixture.Run.TotalInputTokens + fixture.Run.TotalOutputTokens),
            $"tokens left {fixture.Run.Budget.TokensLeft}");

        // --- Control: every tool call went through the PDP ----------------------------------------------
        var toolCalls = fixture.Traces.Where(t => t.Kind == AgentTrace.KindToolCall).ToList();
        Check("control.every_tool_call_has_a_decision", toolCalls.All(t => t.Decision is "Allow" or "Deny" or "RequireApproval"), $"{toolCalls.Count} tool call row(s)");
        var toolsCalledInSteps = fixture.Steps.Sum(s => s.ToolsCalled.Count);
        Check("control.tool_rows_match_step_calls", toolCalls.Count == toolsCalledInSteps, $"{toolCalls.Count} row(s) for {toolsCalledInSteps} call(s)");
        var toolCallsSpent = 40 - fixture.Run.Budget.ToolCallsLeft;
        Check("budget.tool_calls_spent", toolCallsSpent == toolsCalledInSteps, $"{toolCallsSpent} spent for {toolsCalledInSteps} call(s)");
        foreach (var tool in fixture.Expect.RequiredTools)
            Check($"tools.required.{tool}", fixture.Steps.Any(s => s.Status == AgentStep.StatusPassed && s.ToolsCalled.Contains(tool)), $"a passed step called {tool}");
        var sandboxLogs = fixture.Traces.Where(t => t.Kind == AgentTrace.KindSandboxLog).ToList();
        var sandboxCalls = fixture.Steps.SelectMany(s => s.ToolsCalled).Count(t => t == "run_lab_code");
        Check("evidence.sandbox_log_per_sandbox_call", sandboxLogs.Count == sandboxCalls && sandboxLogs.All(l => l.ExitCode is not null), $"{sandboxLogs.Count} log(s) for {sandboxCalls} sandbox call(s)");

        // --- Outcome class ------------------------------------------------------------------------------
        var recovers = stages.Count(s => s == AgentStage.Recover);
        var escalations = fixture.Traces.Count(t => t.Kind == AgentTrace.KindEscalation);
        switch (fixture.Expect.Outcome.ToLowerInvariant())
        {
            case "completed":
                Check("outcome.completed", fixture.Run.Status == AgentRun.StatusCompleted && stages[^1] == AgentStage.Completed, $"status {fixture.Run.Status}");
                Check("outcome.no_recovery_needed", recovers == 0, $"{recovers} Recover stage(s)");
                Check("outcome.all_steps_passed", fixture.Steps.Count > 0 && fixture.Steps.All(s => s.Status == AgentStep.StatusPassed), $"{fixture.Steps.Count} step(s)");
                Check("outcome.learned", stages.Contains(AgentStage.Learn) && fixture.Episode is { Outcome: AgentRun.StatusCompleted, HasSummary: true }, "Learn ran and wrote the episode");
                Check("outcome.budget_untouched", fixture.Run.Budget is { ReplansLeft: 3, RetriesLeft: 2 }, "no recovery counter spent");
                Check("outcome.no_escalation", escalations == 0, $"{escalations} escalation(s)");
                break;

            case "recovered":
                Check("outcome.completed_after_recovery", fixture.Run.Status == AgentRun.StatusCompleted && recovers >= 1, $"status {fixture.Run.Status}, {recovers} Recover stage(s)");
                Check("outcome.recovery_spent_a_counter", fixture.Run.Budget.RetriesLeft < 2 || fixture.Run.Budget.ReplansLeft < 3, $"retries {fixture.Run.Budget.RetriesLeft}, replans {fixture.Run.Budget.ReplansLeft}");
                Check("outcome.failed_attempt_recorded", fixture.Steps.Any(s => s.Status == AgentStep.StatusFailed && !string.IsNullOrEmpty(s.VerifierMessage)), "a failed step carries the verifier message");
                Check("outcome.final_attempt_passed", fixture.Steps.GroupBy(s => (s.PlanRevision, s.PlanStepIndex)).All(g => g.OrderBy(s => s.Attempt).Last().Status == AgentStep.StatusPassed), "the last attempt of every step passed");
                Check("outcome.learned", stages.Contains(AgentStage.Learn) && fixture.Episode is { Outcome: AgentRun.StatusCompleted }, "Learn ran and wrote the episode");
                Check("outcome.no_escalation", escalations == 0, $"{escalations} escalation(s)");
                break;

            case "escalated":
                Check("outcome.failed_by_escalation", fixture.Run.Status == AgentRun.StatusFailed && stages[^1] == AgentStage.Failed && stages[^2] == AgentStage.Recover, $"status {fixture.Run.Status}");
                Check("outcome.escalation_recorded", escalations == 1, $"{escalations} escalation row(s)");
                Check("outcome.reason_names_the_cause", (fixture.Run.StatusReason ?? string.Empty).Contains("Escalated", StringComparison.OrdinalIgnoreCase), fixture.Run.StatusReason ?? "(none)");
                Check("outcome.recovery_budget_exhausted_or_oscillation", fixture.Run.Budget.ReplansLeft == 0 || fixture.Run.Budget.RetriesLeft == 0 || (fixture.Run.StatusReason ?? string.Empty).Contains("Oscillation", StringComparison.OrdinalIgnoreCase) || (fixture.Run.StatusReason ?? string.Empty).Contains("budget", StringComparison.OrdinalIgnoreCase),
                    $"replans {fixture.Run.Budget.ReplansLeft}, retries {fixture.Run.Budget.RetriesLeft}");
                Check("outcome.no_episode_without_instructor", fixture.Episode is null || fixture.Episode.KnowledgeEntryId is not null, "an escalated run has no episode until an instructor publishes the outcome");
                break;

            default:
                Check("outcome.known_class", false, $"unknown outcome class '{fixture.Expect.Outcome}'");
                break;
        }

        return new FixtureScore(fixture.Name, results);
    }

    private static AgentStage ParseStage(string name) => Enum.Parse<AgentStage>(name, ignoreCase: true);
}
