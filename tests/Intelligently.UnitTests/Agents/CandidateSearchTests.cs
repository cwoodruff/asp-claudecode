using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tasks;
using Intelligently.Infrastructure.Agents.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// Candidate search (test-time compute): N planner calls, one judge call per valid candidate, the highest
/// score kept, and every candidate, score and model call recorded as evidence and charged to the run.
/// </summary>
public class CandidateSearchTests
{
    private static AgentTestHost SearchingHost(int candidates = 3, params string[] taskTypes) => new(s =>
        s.Configure<AgentsOptions>(o =>
        {
            o.CandidateSearch.Candidates = candidates;
            o.CandidateSearch.TaskTypes = taskTypes.Length == 0 ? [LabErrorDiagnosisSpec.TaskType] : [.. taskTypes];
        }));

    private static AgentPlan PlanNamed(string goal) => EngineStubs.LookupPlan() with { Goal = goal };

    /// <summary>Each planner call returns the next plan; the judge scores a candidate by the goal it finds in the assistant turn.</summary>
    private static void StubCandidates(AgentTestHost host, IReadOnlyList<AgentPlan?> plans, IReadOnlyDictionary<string, double> scores)
    {
        var call = -1;
        host.Engine.Setup(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var plan = plans[Interlocked.Increment(ref call) % plans.Count];
                return plan is null
                    ? new PlanResult(null, EngineStubs.Usage, "{}", "schema mismatch", null, new ModelCallMetadata(null, "end_turn", "high", 10))
                    : new PlanResult(plan, EngineStubs.Usage, "{\"raw\":\"plan\"}", null, $"[{{\"type\":\"text\",\"text\":{JsonSerializer.Serialize(JsonSerializer.Serialize(plan))}}}]", new ModelCallMetadata($"Planned {plan.Goal}", "end_turn", "high", 20));
            });
        host.Engine.Setup(e => e.JudgeAsync(It.IsAny<PromptEnvelope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptEnvelope envelope, string _, CancellationToken _) =>
            {
                var candidate = envelope.Messages.Last(m => m.Role == PromptMessage.AssistantRole).Text;
                var goal = scores.Keys.Single(candidate.Contains);
                return new JudgementResult(scores[goal] >= 0.6, scores[goal], $"{goal} scored {scores[goal]}", new ModelUsage("claude-opus-5-5", 200, 30), "{}", new ModelCallMetadata(null, "end_turn", "high", 15));
            });
    }

    [Fact]
    public async Task The_highest_scoring_candidate_is_kept_and_every_candidate_is_evidence()
    {
        using var host = SearchingHost();
        StubCandidates(host, [PlanNamed("Plan A"), PlanNamed("Plan B"), PlanNamed("Plan C")], new Dictionary<string, double> { ["Plan A"] = 0.4, ["Plan B"] = 0.9, ["Plan C"] = 0.6 });
        var run = await host.CreateRunAsync();

        await host.StepAsync(run.Id);
        var planned = await host.StepAsync(run.Id);

        planned.Stage.Should().Be(AgentStage.Act);
        planned.StatusReason.Should().Contain("Candidate search kept candidate 1 of 3 (score 0.90)");
        var plan = await host.QueryAsync(db => db.AgentPlans.SingleAsync(p => p.RunId == run.Id));
        plan.Goal.Should().Be("Plan B");
        plan.ResponseContentJson.Should().Contain("Plan B", "the kept candidate's own response blocks are what the transcript replays");

        var traces = await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id && t.Stage == AgentStage.Plan).OrderBy(t => t.Id).ToListAsync());
        var modelCalls = traces.Where(t => t.Kind == AgentTrace.KindModelCall).ToList();
        modelCalls.Should().HaveCount(6, "three planner calls and three judge calls");
        modelCalls.Should().OnlyContain(t => t.CostUsd > 0 && t.StepId == null);
        modelCalls.Count(t => t.InputTokens == 120).Should().Be(3);
        modelCalls.Count(t => t.InputTokens == 200).Should().Be(3);

        var candidates = traces.Where(t => t.Kind == AgentTrace.KindCandidate).ToList();
        candidates.Should().HaveCount(3);
        var rows = candidates.Select(t => JsonDocument.Parse(t.Payload).RootElement).ToList();
        rows.Select(r => r.GetProperty("candidate").GetInt32()).Should().Equal(0, 1, 2);
        rows.Select(r => r.GetProperty("score").GetDouble()).Should().Equal(0.4, 0.9, 0.6);
        rows.Select(r => r.GetProperty("selected").GetBoolean()).Should().Equal(false, true, false);
        rows.Select(r => r.GetProperty("goal").GetString()).Should().Equal("Plan A", "Plan B", "Plan C");
        rows.Should().OnlyContain(r => r.GetProperty("totalCostUsd").GetDecimal() > 0 && r.GetProperty("rationale").GetString()!.Contains("scored"));
        rows[1].GetProperty("steps").EnumerateArray().Should().ContainSingle();

        var judgeRows = modelCalls.Where(t => t.InputTokens == 200).ToList();
        judgeRows.Should().OnlyContain(t => t.Payload.Contains("Judge it against the rubric") && !t.Payload.Contains("Produce the plan"),
            "a judge row stores the envelope the judge received, with the candidate and the judging instruction");
        judgeRows.Select(t => t.Payload).Should().Contain(p => p.Contains("Plan B"));

        planned.TotalInputTokens.Should().Be(3 * 120 + 3 * 200, "the whole search is charged to the run");
        planned.TotalOutputTokens.Should().Be(3 * 40 + 3 * 30);
        planned.TotalCostUsd.Should().Be(modelCalls.Sum(t => t.CostUsd));
        planned.Budget.TokensLeft.Should().Be(300_000 - planned.TotalInputTokens - planned.TotalOutputTokens);
    }

    [Fact]
    public async Task Invalid_candidates_are_skipped_and_the_search_falls_back_to_a_valid_one()
    {
        using var host = SearchingHost();
        StubCandidates(host, [null, PlanNamed("Plan B"), null], new Dictionary<string, double> { ["Plan B"] = 0.5 });
        var run = await host.CreateRunAsync();

        await host.StepAsync(run.Id);
        var planned = await host.StepAsync(run.Id);

        planned.Stage.Should().Be(AgentStage.Act);
        planned.StatusReason.Should().Contain("kept candidate 1 of 3 (score 0.50)");
        var traces = await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id && t.Stage == AgentStage.Plan).ToListAsync());
        traces.Count(t => t.Kind == AgentTrace.KindModelCall).Should().Be(4, "three planner calls, one judge call for the only valid candidate");
        var rows = traces.Where(t => t.Kind == AgentTrace.KindCandidate).Select(t => JsonDocument.Parse(t.Payload).RootElement).ToList();
        rows.Count(r => !r.GetProperty("succeeded").GetBoolean()).Should().Be(2);
        rows.Where(r => !r.GetProperty("succeeded").GetBoolean()).Should().OnlyContain(r => r.GetProperty("failureReason").GetString() == "schema mismatch" && r.GetProperty("score").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_candidate_that_fails_plan_validation_is_neither_judged_nor_selected_whatever_it_would_score()
    {
        using var host = SearchingHost();
        var unknownTool = PlanNamed("Plan X") with { Steps = [EngineStubs.LookupPlan().Steps[0] with { Tool = "no_such_tool" }] };
        var badPhase = LabSpecs.ShortPlan(LabPhases.Rerun) with { Goal = "Plan Y", Steps = [LabSpecs.ShortPlan(LabPhases.Rerun).Steps[0] with { Phase = "guess" }, LabSpecs.ShortPlan().Steps[1]] };
        StubCandidates(host, [unknownTool, PlanNamed("Plan A"), badPhase], new Dictionary<string, double> { ["Plan X"] = 0.99, ["Plan A"] = 0.5, ["Plan Y"] = 0.95 });
        var run = await host.CreateRunAsync();

        await host.StepAsync(run.Id);
        var planned = await host.StepAsync(run.Id);

        planned.Stage.Should().Be(AgentStage.Act);
        planned.StatusReason.Should().Contain("kept candidate 1 of 3 (score 0.50)");
        (await host.QueryAsync(db => db.AgentPlans.SingleAsync(p => p.RunId == run.Id))).Goal.Should().Be("Plan A");
        host.Engine.Verify(e => e.JudgeAsync(It.IsAny<PromptEnvelope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once, "only the executable candidate is judged");
        var rows = (await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id && t.Kind == AgentTrace.KindCandidate).OrderBy(t => t.Id).ToListAsync()))
            .Select(t => JsonDocument.Parse(t.Payload).RootElement).ToList();
        rows[0].GetProperty("succeeded").GetBoolean().Should().BeFalse();
        rows[0].GetProperty("failureReason").GetString().Should().Contain("unknown tool 'no_such_tool'");
        rows[0].GetProperty("judged").GetBoolean().Should().BeFalse();
        rows[0].GetProperty("score").ValueKind.Should().Be(JsonValueKind.Null);
        rows[2].GetProperty("failureReason").GetString().Should().Contain("phase 'guess'");
        rows.Select(r => r.GetProperty("selected").GetBoolean()).Should().Equal(false, true, false);
    }

    [Fact]
    public async Task No_judge_runs_when_the_planner_batch_spends_the_hard_budget()
    {
        using var host = new AgentTestHost(s => s.Configure<AgentsOptions>(o =>
        {
            o.CandidateSearch.Candidates = 3;
            o.CandidateSearch.TaskTypes = [LabErrorDiagnosisSpec.TaskType];
            o.Budgets.TokensLeft = 400;   // three planner calls of 160 tokens spend it
        }));
        StubCandidates(host, [PlanNamed("Plan A"), PlanNamed("Plan B"), PlanNamed("Plan C")], new Dictionary<string, double> { ["Plan A"] = 0.4, ["Plan B"] = 0.9, ["Plan C"] = 0.6 });
        var run = await host.CreateRunAsync();

        await host.StepAsync(run.Id);
        var planned = await host.StepAsync(run.Id);

        planned.Stage.Should().Be(AgentStage.Recover);
        planned.StatusReason.Should().Contain("Hard budget exhausted after the planner call");
        planned.Budget.TokensLeft.Should().Be(0);
        host.Engine.Verify(e => e.JudgeAsync(It.IsAny<PromptEnvelope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, "no paid call runs on a spent budget");
        var traces = await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id && t.Stage == AgentStage.Plan).ToListAsync());
        traces.Count(t => t.Kind == AgentTrace.KindModelCall).Should().Be(3, "the planner calls are still recorded and charged");
        traces.Where(t => t.Kind == AgentTrace.KindCandidate).Select(t => JsonDocument.Parse(t.Payload).RootElement.GetProperty("judged").GetBoolean()).Should().OnlyContain(j => !j);
    }

    [Fact]
    public async Task When_no_candidate_is_valid_the_run_recovers_with_the_failure_and_the_search_cost()
    {
        using var host = SearchingHost();
        StubCandidates(host, [null, null, null], new Dictionary<string, double>());
        var run = await host.CreateRunAsync();

        await host.StepAsync(run.Id);
        var planned = await host.StepAsync(run.Id);

        planned.Stage.Should().Be(AgentStage.Recover);
        planned.StatusReason.Should().Contain("schema mismatch").And.Contain("none of 3 candidates was valid");
        host.Engine.Verify(e => e.JudgeAsync(It.IsAny<PromptEnvelope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        (await host.QueryAsync(db => db.AgentTraces.CountAsync(t => t.RunId == run.Id && t.Kind == AgentTrace.KindCandidate))).Should().Be(3);
    }

    [Fact]
    public async Task Search_is_off_by_default_and_per_task_type()
    {
        using var defaults = new AgentTestHost();
        EngineStubs.PlanReturns(defaults.Engine, EngineStubs.LookupPlan());
        var run = await defaults.CreateRunAsync();
        await defaults.StepAsync(run.Id);
        await defaults.StepAsync(run.Id);
        defaults.Engine.Verify(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
        defaults.Engine.Verify(e => e.JudgeAsync(It.IsAny<PromptEnvelope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        (await defaults.QueryAsync(db => db.AgentTraces.CountAsync(t => t.RunId == run.Id && t.Kind == AgentTrace.KindCandidate))).Should().Be(0);

        using var other = SearchingHost(3, "QuizGeneration");
        EngineStubs.PlanReturns(other.Engine, EngineStubs.LookupPlan());
        var lab = await other.CreateRunAsync();
        await other.StepAsync(lab.Id);
        await other.StepAsync(lab.Id);
        other.Engine.Verify(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()), Times.Once, "the search is enabled for another task type only");

        var options = new AgentsOptions.CandidateSearchOptions { Candidates = 3, TaskTypes = ["LabErrorDiagnosis"] };
        options.IsEnabledFor("laberrordiagnosis").Should().BeTrue();
        options.IsEnabledFor("Other").Should().BeFalse();
        new AgentsOptions.CandidateSearchOptions { Candidates = 1, TaskTypes = ["LabErrorDiagnosis"] }.IsEnabledFor("LabErrorDiagnosis").Should().BeFalse("one candidate is no search");
    }

    [Fact]
    public async Task The_judge_sees_the_candidate_as_the_assistant_turn_with_a_judging_instruction()
    {
        using var host = SearchingHost(candidates: 2);
        var envelopes = new List<PromptEnvelope>();
        var rubrics = new List<string>();
        host.Engine.Setup(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanResult(PlanNamed("Plan A"), EngineStubs.Usage, "{}"));
        host.Engine.Setup(e => e.JudgeAsync(It.IsAny<PromptEnvelope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<PromptEnvelope, string, CancellationToken>((e, r, _) => { lock (envelopes) { envelopes.Add(e); rubrics.Add(r); } })
            .ReturnsAsync(new JudgementResult(true, 0.7, "fine", EngineStubs.Usage, "{}"));
        var run = await host.CreateRunAsync();

        await host.StepAsync(run.Id);
        await host.StepAsync(run.Id);

        envelopes.Should().HaveCount(2);
        foreach (var envelope in envelopes)
        {
            envelope.Messages[^1].Should().Match<PromptMessage>(m => m.IsSystem && m.Section == PromptSection.Instruction && m.Text.Contains("Judge it against the rubric"));
            envelope.Messages[^2].Should().Match<PromptMessage>(m => m.Role == PromptMessage.AssistantRole && m.Text.Contains("Plan A"));
            envelope.Messages.Should().NotContain(m => m.Text.Contains("Produce the plan"), "the Plan-stage instruction is replaced");
        }
        rubrics.Should().OnlyContain(r => r.Contains("reproduce → diagnose → propose_fix → rerun → explain") && r.Contains("Beginner"));
    }
}
