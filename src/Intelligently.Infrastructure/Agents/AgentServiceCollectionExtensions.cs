using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Context;
using Intelligently.Infrastructure.Agents.Control;
using Intelligently.Infrastructure.Agents.Evidence;
using Intelligently.Infrastructure.Agents.Execution;
using Intelligently.Infrastructure.Agents.Intelligence;
using Intelligently.Infrastructure.Agents.StateGraph;
using Intelligently.Infrastructure.Agents.StateGraph.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddScoped<StateGraphRunner>();
        services.AddScoped<AgentRunService>();

        // Stage handlers (one per stage; AwaitingApproval is parked, not executed, until Phase 2)
        services.AddScoped<IStageHandler, InterpretHandler>();
        services.AddScoped<IStageHandler, PlanHandler>();
        services.AddScoped<IStageHandler, ActHandler>();
        services.AddScoped<IStageHandler, ObserveHandler>();
        services.AddScoped<IStageHandler, VerifyHandler>();
        services.AddScoped<IStageHandler, RecoverHandler>();
        services.AddScoped<IStageHandler, LearnHandler>();

        // Intelligence Plane
        services.AddSingleton<IReasoningEngine, AnthropicReasoningEngine>();

        // Context Plane
        services.AddScoped<IContextAssembler, ContextAssembler>();

        // Execution Plane
        services.AddScoped<IToolHandler, LookupKnowledgeTool>();
        services.AddScoped<IToolRegistry, ToolRegistry>();
        services.AddScoped<IToolDispatcher, ToolDispatcher>();

        // Control Plane
        services.AddSingleton<IPolicyDecisionPoint, PolicyDecisionPoint>();

        // Evidence Plane
        services.AddScoped<IEvidenceSink, OtelEvidenceSink>();

        return services;
    }
}
