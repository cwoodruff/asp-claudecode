using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tasks;
using Intelligently.Infrastructure.Agents.Tasks;

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
/// well-formed run, checks per outcome class, and (Phase 6) checks specific to the lab-error-diagnosis
/// template: phase tagging, reproduce before any fix, ground truth or judgement, and the learner-facing
/// output. Each criterion is scored independently so a regression shows up as a pass-rate drop with the
/// failing criterion named, not as a single red test.
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
        // One call per Plan or Act stage, plus one per judgement (a Verify-stage call), plus the extra calls
        // of a candidate search (each candidate is a planner call and, when valid, a judge call; the stage's
        // own single call is already counted once per search).
        var modelCalls = fixture.Traces.Where(t => t.Kind == AgentTrace.KindModelCall).ToList();
        var judgements = fixture.Traces.Where(t => t.Kind == AgentTrace.KindJudgement).ToList();
        var candidates = fixture.Traces.Where(t => t.Kind == AgentTrace.KindCandidate).ToList();
        var modelStages = stages.Count(s => s is AgentStage.Plan or AgentStage.Act);
        var searchedPlanStages = candidates.Count(c => c.Candidate == 0);
        var candidateCalls = candidates.Sum(c => c.Score is null ? 1 : 2);
        var expectedModelCalls = modelStages - searchedPlanStages + candidateCalls + judgements.Count;
        Check("evidence.model_call_per_reasoning_stage", modelCalls.Count == expectedModelCalls,
            $"{modelCalls.Count} model call(s) for {modelStages} Plan/Act stage(s), {judgements.Count} judgement(s), {candidates.Count} candidate(s)");
        Check("evidence.model_calls_priced", modelCalls.All(t => t.CostUsd > 0 && t.Model is not null), "every model call has a model and a non-zero cost");
        Check("evidence.model_calls_have_stop_reason_and_effort", modelCalls.All(t => !string.IsNullOrEmpty(t.StopReason) && !string.IsNullOrEmpty(t.Effort)), "stop reason and effort recorded");
        Check("evidence.model_calls_have_latency", modelCalls.All(t => t.LatencyMs is > 0), "latency recorded");
        Check("evidence.reasoning_summary_present", modelCalls.Count == 0 || modelCalls.Any(t => !string.IsNullOrWhiteSpace(t.ReasoningSummary)), "at least one call returned a summarized reasoning block");
        if (judgements.Count > 0)
        {
            Check("evidence.judgement_scores_in_range", judgements.All(j => j.Score is >= 0 and <= 1), $"{judgements.Count} judgement(s)");
            Check("evidence.judgement_has_verify_model_call", modelCalls.Count(t => ParseStage(t.Stage) == AgentStage.Verify) == judgements.Count, "one Verify-stage model call per judgement");
        }
        if (candidates.Count > 0)
        {
            // Candidate rows of one search are contiguous and start at index 0. A search with a valid (judged)
            // candidate keeps exactly one; an all-invalid search deliberately keeps none and the run recovers.
            var searches = new List<List<FixtureTrace>>();
            foreach (var candidate in candidates)
            {
                if (candidate.Candidate == 0 || searches.Count == 0) searches.Add([]);
                searches[^1].Add(candidate);
            }
            Check("evidence.candidate_search_kept_one_valid_candidate",
                searches.All(s => s.Count(c => c.Decision == "Selected") == (s.Any(c => c.Score is not null) ? 1 : 0))
                && candidates.Where(c => c.Score is not null).All(c => c.Score is >= 0 and <= 1),
                $"{candidates.Count} candidate row(s) over {searches.Count} search(es)");
        }

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
        // A call parked for approval is budgeted when proposed; it only gets a tool-call row once it runs, so a denied one is spent without a row.
        var approvals = fixture.Traces.Where(t => t.Kind == AgentTrace.KindApproval).ToList();
        var deniedApprovals = approvals.Count(a => a.Decision == "Denied");
        var toolCallsSpent = 40 - fixture.Run.Budget.ToolCallsLeft;
        Check("budget.tool_calls_spent", toolCallsSpent == toolsCalledInSteps + deniedApprovals, $"{toolCallsSpent} spent for {toolsCalledInSteps} call(s) and {deniedApprovals} denied approval(s)");
        if (approvals.Count > 0)
        {
            Check("control.approval_requested_before_decided", approvals.First().Decision == "Pending" && approvals.Count(a => a.Decision == "Pending") == approvals.Count(a => a.Decision is "Approved" or "Denied"),
                $"{approvals.Count} approval row(s)");
            Check("control.approval_parks_the_run", stages.Contains(AgentStage.AwaitingApproval), "the run visited AwaitingApproval");
            if (deniedApprovals > 0)
                Check("control.denied_approval_goes_to_recover", stages.Zip(stages.Skip(1)).Any(e => e.First == AgentStage.AwaitingApproval && e.Second == AgentStage.Recover), "AwaitingApproval → Recover after the denial");
        }
        foreach (var tool in fixture.Expect.RequiredTools)
            Check($"tools.required.{tool}", fixture.Steps.Any(s => s.Status == AgentStep.StatusPassed && s.ToolsCalled.Contains(tool)), $"a passed step called {tool}");
        var sandboxLogs = fixture.Traces.Where(t => t.Kind == AgentTrace.KindSandboxLog).ToList();
        var sandboxCalls = fixture.Steps.SelectMany(s => s.ToolsCalled).Count(t => t == "run_lab_code");
        Check("evidence.sandbox_log_per_sandbox_call", sandboxLogs.Count == sandboxCalls && sandboxLogs.All(l => l.ExitCode is not null), $"{sandboxLogs.Count} log(s) for {sandboxCalls} sandbox call(s)");

        // --- Outcome class ------------------------------------------------------------------------------
        var recovers = stages.Count(s => s == AgentStage.Recover);
        var escalations = fixture.Traces.Count(t => t.Kind == AgentTrace.KindEscalation);
        var reason = fixture.Run.StatusReason ?? string.Empty;
        var outcome = fixture.Expect.Outcome.ToLowerInvariant();
        switch (outcome)
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
                Check("outcome.reason_names_the_cause", reason.Contains("Escalated", StringComparison.OrdinalIgnoreCase), fixture.Run.StatusReason ?? "(none)");
                Check("outcome.escalation_cause_recognised",
                    fixture.Run.Budget.ReplansLeft == 0 || fixture.Run.Budget.RetriesLeft == 0
                    || reason.Contains("Oscillation", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("budget", StringComparison.OrdinalIgnoreCase)
                    || (deniedApprovals > 0 && reason.Contains("denied", StringComparison.OrdinalIgnoreCase)),
                    $"replans {fixture.Run.Budget.ReplansLeft}, retries {fixture.Run.Budget.RetriesLeft}, denied approvals {deniedApprovals}");
                Check("outcome.no_episode_without_instructor", fixture.Episode is null || fixture.Episode.KnowledgeEntryId is not null, "an escalated run has no episode until an instructor publishes the outcome");
                Check("outcome.no_learner_output", !fixture.Run.HasExplanation && !fixture.Run.HasFixDiff, "a failed run shows the learner nothing");
                break;

            default:
                Check("outcome.known_class", false, $"unknown outcome class '{fixture.Expect.Outcome}'");
                break;
        }

        // --- Lab error diagnosis (Phase 6): the template, the verification path and the learner output ---
        if (string.Equals(fixture.Run.TaskType, LabErrorDiagnosisSpec.TaskType, StringComparison.OrdinalIgnoreCase))
            EvaluateLab(fixture, stages, judgements, outcome, Check);

        return new FixtureScore(fixture.Name, results);
    }

    private static void EvaluateLab(TraceFixture fixture, List<AgentStage> stages, List<FixtureTrace> judgements, string outcome, Action<string, bool, string> check)
    {
        var steps = fixture.Steps.OrderBy(s => s.PlanRevision).ThenBy(s => s.PlanStepIndex).ThenBy(s => s.Attempt).ToList();
        var tagged = steps.Where(s => s.Phase is not null).ToList();
        check("lab.phases_are_template_phases", tagged.All(s => LabPhases.IsKnown(s.Phase)), $"{tagged.Count} tagged step(s)");

        var sandboxSteps = steps.Where(s => s.ToolsCalled.Contains("run_lab_code")).ToList();
        if (sandboxSteps.Count > 0)
        {
            check("lab.sandbox_steps_are_reproduce_or_rerun", sandboxSteps.All(s => s.Phase is LabPhases.Reproduce or LabPhases.Rerun), $"{sandboxSteps.Count} sandbox step(s)");
            check("lab.reproduce_before_any_fix", sandboxSteps[0].Phase == LabPhases.Reproduce, $"first sandbox step is '{sandboxSteps[0].Phase}'");
        }

        var passedByPhase = steps.Where(s => s.Status == AgentStep.StatusPassed).GroupBy(s => s.Phase).ToDictionary(g => g.Key ?? "(none)", g => g.Count());
        var passedExplains = passedByPhase.GetValueOrDefault(LabPhases.Explain);
        var passedReruns = passedByPhase.GetValueOrDefault(LabPhases.Rerun);
        if (fixture.Run.ExpectedOutputPresent)
        {
            check("lab.ground_truth_decides_graded_labs", judgements.Count == 0, $"{judgements.Count} judgement(s) on a lab with an expected output");
            if (passedReruns > 0)
                check("lab.rerun_passed_the_ground_truth_gate", steps.Where(s => s.Phase == LabPhases.Rerun && s.Status == AgentStep.StatusPassed).All(s => (s.VerifierMessage ?? string.Empty).Contains("Ground truth", StringComparison.Ordinal)), "the rerun verifier message names the gate");
        }
        else if (passedExplains > 0)
        {
            // One judgement at or above the threshold per passed explanation attempt; a judgement below it is a
            // failed attempt that Recover retried, which is evidence of the gate working, not a defect.
            var passingJudgements = judgements.Count(j => j.Score is >= LabErrorDiagnosisContract.ExplanationMinimumScore);
            var failedExplains = steps.Count(s => s.Phase == LabPhases.Explain && s.Status == AgentStep.StatusFailed);
            check("lab.explanation_judged_without_ground_truth",
                passingJudgements == passedExplains && judgements.Count - passingJudgements <= failedExplains,
                $"{judgements.Count} judgement(s) ({passingJudgements} at or above {LabErrorDiagnosisContract.ExplanationMinimumScore}) for {passedExplains} passed and {failedExplains} failed explanation step(s)");
        }

        if (outcome is "completed" or "recovered")
        {
            check("lab.explanation_stored_for_the_learner", fixture.Run.HasExplanation, "FinalExplanation set on completion");
            if (passedReruns > 0)
                check("lab.fix_diff_stored_when_the_fix_ran", fixture.Run.HasFixDiff, "FixDiff set when a re-run succeeded");
        }

        // A denied course-material approval must escalate without a replan after it; a denied Risk approval replans instead.
        var materialDenials = fixture.Traces.Where(t => t.Kind == AgentTrace.KindApproval && t.Decision == "Denied" && t.RuleKind == nameof(PolicyRuleKind.CourseMaterial)).ToList();
        if (materialDenials.Count > 0)
        {
            var lastDenial = stages.LastIndexOf(AgentStage.AwaitingApproval);
            var replannedAfterDenial = lastDenial >= 0 && stages.Skip(lastDenial).Contains(AgentStage.Plan);
            check("lab.course_material_denial_escalates",
                outcome == "escalated" && !replannedAfterDenial && (fixture.Run.StatusReason ?? string.Empty).Contains("course-provided material", StringComparison.OrdinalIgnoreCase),
                replannedAfterDenial ? "the run replanned after the denial" : "escalated without a replan after the denial");
        }
    }

    private static AgentStage ParseStage(string name) => Enum.Parse<AgentStage>(name, ignoreCase: true);
}
