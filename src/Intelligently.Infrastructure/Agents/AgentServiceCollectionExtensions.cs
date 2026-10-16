using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Context;
using Intelligently.Infrastructure.Agents.Control;
using Intelligently.Infrastructure.Agents.Evidence;
using Intelligently.Infrastructure.Agents.Execution;
using Intelligently.Infrastructure.Agents.Execution.Mcp;
using Intelligently.Infrastructure.Agents.Execution.Sandboxes;
using Intelligently.Infrastructure.Agents.Intelligence;
using Intelligently.Infrastructure.Agents.Learn;
using Intelligently.Infrastructure.Agents.StateGraph;
using Intelligently.Infrastructure.Agents.StateGraph.Handlers;
using Intelligently.Infrastructure.Agents.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents;

public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the five planes and the state graph. Requires <c>IntelligentlyDbContext</c>
    /// and <c>IAnthropicClient</c> to be registered by the host.
    /// </summary>
    public static IServiceCollection AddAgentRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentsOptions>(configuration.GetSection(AgentsOptions.SectionName));

        // Execution host
        services.AddSingleton<IRunQueue, RunQueue>();
        services.AddSingleton<IWorkerIdentity, ProcessWorkerIdentity>();
        services.AddScoped<StateGraphRunner>();
        services.AddScoped<AgentRunService>();

        // Stage handlers (one per stage, including the parked AwaitingApproval stage)
        services.AddScoped<IStageHandler, InterpretHandler>();
        services.AddScoped<IStageHandler, PlanHandler>();
        services.AddScoped<IStageHandler, ActHandler>();
        services.AddScoped<IStageHandler, AwaitingApprovalHandler>();
        services.AddScoped<IStageHandler, ObserveHandler>();
        services.AddScoped<IStageHandler, VerifyHandler>();
        services.AddScoped<IStageHandler, RecoverHandler>();
        services.AddScoped<IStageHandler, LearnHandler>();

        // Task contracts: one per typed task; the registry is what the stages and the Control Plane consult
        services.AddSingleton<ITaskContract, LabErrorDiagnosisContract>();
        services.AddSingleton<ITaskTypeRegistry, TaskTypeRegistry>();

        // Intelligence Plane (candidate search records evidence, so it lives on the stage's unit of work)
        services.AddSingleton<IReasoningEngine, AnthropicReasoningEngine>();
        services.AddScoped<ICandidateSearch, CandidateSearch>();

        // Context Plane: retrieval and episodic memory on the scoped DbContext; counting, budgeting,
        // compaction and cache health shared across scopes
        services.AddSingleton<CacheHealthMonitor>();
        services.AddSingleton<IPromptTokenCounter, AnthropicTokenCounter>();
        services.AddSingleton<TokenBudgeter>();
        services.AddSingleton<Compactor>();
        services.AddSingleton<IEpisodeSummarizer>(sp => sp.GetRequiredService<Compactor>());
        services.AddScoped<IKnowledgeRetriever, KnowledgeRetriever>();
        services.AddScoped<IEpisodicMemoryStore, EpisodicMemoryStore>();
        services.AddScoped<IContextAssembler, ContextAssembler>();

        // Execution Plane: native tools (each opens its own scope when it needs the DbContext, so the
        // calls of one turn can run concurrently), sandboxes, MCP servers connected at host start
        services.AddScoped<IToolHandler, LookupKnowledgeTool>();
        services.AddScoped<IToolHandler, RunLabCodeTool>();
        services.AddScoped<IToolHandler, CreateEscalationTool>();
        services.AddScoped<IToolHandler, NotifyLearnerTool>();
        services.AddSingleton<DockerSandbox>();
        services.AddSingleton<WasmSandbox>();
        services.AddSingleton<ISandbox>(sp => sp.GetRequiredService<DockerSandbox>());
        services.AddSingleton<IMcpTransportFactory, ConfiguredMcpTransportFactory>();
        services.AddSingleton<McpToolSource>();
        services.AddSingleton<IToolSource>(sp => sp.GetRequiredService<McpToolSource>());
        services.AddHostedService<McpToolSourceHostedService>();
        services.AddSingleton<ToolOutputValidator>();
        services.AddScoped<IToolRegistry, ToolRegistry>();
        services.AddScoped<IToolDispatcher, ToolDispatcher>();

        // Control Plane: rules from Agents:Policy, rate limiter shared across scopes, barrier and gate on the scoped DbContext
        services.AddSingleton<IToolRateLimiter>(sp => new ToolRateLimiter(sp.GetRequiredService<IOptions<AgentsOptions>>()));
        services.AddScoped<IIdempotencyBarrier, IdempotencyBarrier>();
        services.AddScoped<IApprovalGate, ApprovalGate>();
        services.AddScoped<IPolicyDecisionPoint, PolicyDecisionPoint>();

        // Evidence Plane: spans, append-only trace rows and the run totals on the scoped DbContext; the
        // price table is shared
        services.AddScoped<IEvidenceSink, OtelEvidenceSink>();
        services.AddSingleton<ICostAccountant, CostAccountant>();

        // Learn: the single episode writer (Learn stage and instructor resolution both go through it)
        services.AddScoped<EpisodeWriter>();
        services.AddScoped<IEscalationResolver, EscalationResolver>();

        return services;
    }
}
