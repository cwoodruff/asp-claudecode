using Intelligently.Core.Agents.Entities;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>One plan candidate of a search: the planner result, its judgement and whether it was kept.</summary>
public sealed record PlanCandidate(int Index, PlanResult Plan, JudgementResult? Judgement, bool Selected)
{
    public double Score => Judgement?.Score ?? 0;
}

/// <summary>The outcome of a candidate search: the plan kept and every candidate that was generated and scored.</summary>
public sealed record CandidateSearchResult(PlanResult Best, IReadOnlyList<PlanCandidate> Candidates);

/// <summary>
/// Intelligence Plane test-time compute (plan section 1.1): generate N plans in parallel, score each with
/// <see cref="IReasoningEngine.JudgeAsync"/> and keep the best. Enabled per task type by configuration so the
/// cost stays explicit; every candidate, its score and its model calls are recorded as evidence.
/// </summary>
public interface ICandidateSearch
{
    bool IsEnabledFor(string? taskType);

    Task<CandidateSearchResult> SearchAsync(AgentRun run, PromptEnvelope envelope, CancellationToken ct = default);
}
