using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Agents;
using Intelligently.Infrastructure.Agents.Context;
using Intelligently.Infrastructure.Agents.StateGraph;
using Intelligently.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// Hosts the real agent runtime (runner, handlers, tools, policy, evidence, EF on SQLite in-memory)
/// with a mocked <see cref="IReasoningEngine"/>. Each <see cref="BuildProvider"/> call creates a
/// fresh container over the same open connection, which is how the crash-resume test simulates a restart.
/// </summary>
public sealed class AgentTestHost : IDisposable
{
    public SqliteConnection Connection { get; }
    public Mock<IReasoningEngine> Engine { get; } = new(MockBehavior.Strict);
    public ServiceProvider Provider { get; private set; }

    public AgentTestHost(Action<IServiceCollection>? configure = null)
    {
        Connection = new SqliteConnection("DataSource=:memory:");
        Connection.Open();
        Provider = BuildProvider(configure);
        using var scope = Provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IntelligentlyDbContext>().Database.EnsureCreated();
    }

    public ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agents:DefaultModel"] = "claude-opus-5-5",
            ["Agents:Budgets:ReplansLeft"] = "3",
            ["Agents:Budgets:RetriesLeft"] = "2",
            ["Agents:Budgets:ToolCallsLeft"] = "40",
            ["Agents:Budgets:TokensLeft"] = "300000",
            ["Agents:Budgets:WallClockMinutes"] = "30",
            ["Agents:QueueCapacity"] = "10",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<IntelligentlyDbContext>(o => o.UseSqlite(Connection));
        services.AddAgentRuntime(configuration);
        services.AddSingleton(Engine.Object);          // replaces AnthropicReasoningEngine
        services.AddSingleton<IPromptTokenCounter, EstimatingTokenCounter>();   // no provider call for counting
        services.AddSingleton<IEpisodeSummarizer, ExtractiveSummarizer>();      // no provider call for episode summaries
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>Simulates a process restart: throws away the container but keeps the database.</summary>
    public void Restart(Action<IServiceCollection>? configure = null)
    {
        Provider.Dispose();
        Provider = BuildProvider(configure);
    }

    public async Task<AgentRun> CreateRunAsync(string taskType = "LabErrorDiagnosis", string taskSpec = """{"question":"CS0246 on build"}""", string userId = "learner-1", int courseId = 7)
    {
        using var scope = Provider.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<AgentRunService>();
        return await runs.CreateAsync(userId, courseId, taskType, taskSpec);
    }

    public async Task<AgentRun> StepAsync(Guid runId)
    {
        using var scope = Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<StateGraphRunner>().StepAsync(runId);
    }

    /// <summary>Steps until the run is no longer Running, returning the sequence of stages visited.</summary>
    public async Task<(AgentRun Run, List<AgentStage> Stages)> RunToEndAsync(Guid runId, int maxStages = 50)
    {
        var stages = new List<AgentStage>();
        AgentRun run;
        do
        {
            run = await LoadAsync(runId);
            stages.Add(run.Stage);
            run = await StepAsync(runId);
        } while (run.IsActive && stages.Count < maxStages);
        stages.Add(run.Stage);
        return (run, stages);
    }

    public async Task<AgentRun> LoadAsync(Guid runId)
    {
        using var scope = Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IntelligentlyDbContext>()
            .AgentRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
    }

    public async Task<T> QueryAsync<T>(Func<IntelligentlyDbContext, Task<T>> query)
    {
        using var scope = Provider.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<IntelligentlyDbContext>());
    }

    public async Task SaveAsync(Func<IntelligentlyDbContext, Task> action)
    {
        using var scope = Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntelligentlyDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        Provider.Dispose();
        Connection.Dispose();
    }
}

/// <summary>A stage handler that returns a fixed outcome (or throws), for runner tests.</summary>
public sealed class StubHandler : IStageHandler
{
    private readonly Func<AgentRun, StageOutcome> _outcome;

    public StubHandler(AgentStage stage, AgentStage next, string? reason = null)
        : this(stage, _ => StageOutcome.To(next, reason)) { }

    public StubHandler(AgentStage stage, Func<AgentRun, StageOutcome> outcome)
    {
        Stage = stage;
        _outcome = outcome;
    }

    public AgentStage Stage { get; }
    public int Calls { get; private set; }

    public Task<StageOutcome> ExecuteAsync(AgentRun run, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(_outcome(run));
    }
}
