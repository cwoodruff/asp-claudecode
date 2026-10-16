using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents;

/// <summary>Creates runs with the configured budget and hands them to the queue.</summary>
public sealed class AgentRunService
{
    private readonly IntelligentlyDbContext _db;
    private readonly IRunQueue _queue;
    private readonly IEvidenceSink _evidence;
    private readonly AgentsOptions _options;

    public AgentRunService(IntelligentlyDbContext db, IRunQueue queue, IEvidenceSink evidence, IOptions<AgentsOptions> options)
    {
        _db = db;
        _queue = queue;
        _evidence = evidence;
        _options = options.Value;
    }

    /// <summary>
    /// Creates a run acting as <paramref name="principal"/> (default: a learner enrolled in
    /// <paramref name="courseId"/>) and enqueues it.
    /// </summary>
    public async Task<AgentRun> CreateAsync(string userId, int courseId, string taskType, string taskSpec, RunPrincipal? principal = null, CancellationToken ct = default)
    {
        var budgets = _options.Budgets;
        var run = new AgentRun
        {
            UserId = userId,
            CourseId = courseId,
            TaskType = taskType,
            TaskSpec = taskSpec,
            Stage = AgentStage.Interpret,
            Status = AgentRun.StatusRunning,
            Principal = principal ?? RunPrincipal.Learner(userId, courseId),
            Budget = new RecoveryBudget
            {
                ReplansLeft = budgets.ReplansLeft,
                RetriesLeft = budgets.RetriesLeft,
                ToolCallsLeft = budgets.ToolCallsLeft,
                TokensLeft = budgets.TokensLeft,
                WallClockLeft = TimeSpan.FromMinutes(budgets.WallClockMinutes),
            },
        };

        // The run's root span: every stage span, whichever worker executes it, is parented on it.
        _evidence.StartRun(run);

        _db.AgentRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        await _queue.EnqueueAsync(run.Id, ct);
        return run;
    }

    public Task<AgentRun?> FindAsync(Guid id, CancellationToken ct = default) =>
        _db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
}
