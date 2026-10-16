using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Agents.Learn;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>
/// Learn: write the episodic memory row for the completed run through the <see cref="EpisodeWriter"/>
/// (summary from the local compaction mode, Haiku at low effort) and, for a task type with a contract,
/// store the learner-facing output (the explanation and the fix as a diff) on the run. The knowledge-base
/// link is made later, when an instructor marks an escalation's outcome reusable (<c>IEscalationResolver</c>).
/// </summary>
public sealed class LearnHandler : IStageHandler
{
    private readonly EpisodeWriter _episodes;
    private readonly ITaskTypeRegistry _tasks;
    private readonly IntelligentlyDbContext _db;

    public LearnHandler(EpisodeWriter episodes, ITaskTypeRegistry tasks, IntelligentlyDbContext db)
    {
        _episodes = episodes;
        _tasks = tasks;
        _db = db;
    }

    public AgentStage Stage => AgentStage.Learn;

    public async Task<StageOutcome> ExecuteAsync(AgentRun run, CancellationToken ct = default)
    {
        var output = string.Empty;
        if (_tasks.Find(run.TaskType) is { } contract)
        {
            var plan = await StepQueries.CurrentPlanAsync(_db, run, ct);
            var steps = await _db.AgentSteps.AsNoTracking()
                .Where(s => s.RunId == run.Id)
                .OrderBy(s => s.StartedAt).ThenBy(s => s.Attempt)
                .ToListAsync(ct);
            var learner = contract.ComposeLearnerOutput(run, plan, steps);
            run.FinalExplanation = learner.Explanation;
            run.FixDiff = learner.FixDiff;
            output = learner.Explanation is null
                ? " No learner explanation was produced."
                : learner.FixDiff is null ? " Explanation stored; no file changes." : " Explanation and fix diff stored.";
        }

        var episode = await _episodes.WriteAsync(run, AgentRun.StatusCompleted, ct);
        return StageOutcome.To(AgentStage.Completed, $"Episode {episode.Id} and traces written.{output}");
    }
}
