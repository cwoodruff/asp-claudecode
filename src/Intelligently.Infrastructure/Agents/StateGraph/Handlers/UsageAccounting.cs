using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>Shared helpers for charging model usage to a run and recording it as evidence.</summary>
internal static class UsageAccounting
{
    public static void Apply(AgentRun run, ModelUsage usage)
    {
        run.TotalInputTokens += usage.InputTokens;
        run.TotalOutputTokens += usage.OutputTokens;
        run.Budget.TrySpendTokens(usage.InputTokens + usage.OutputTokens);
    }

    public static AgentTrace ModelCallTrace(AgentStage stage, PromptEnvelope envelope, ModelUsage usage, string rawResponse, Guid? stepId = null) => new()
    {
        Stage = stage,
        StepId = stepId,
        Kind = AgentTrace.KindModelCall,
        Model = usage.Model,
        InputTokens = usage.InputTokens,
        OutputTokens = usage.OutputTokens,
        CacheReadInputTokens = usage.CacheReadInputTokens,
        CacheCreationInputTokens = usage.CacheCreationInputTokens,
        Payload = JsonSerializer.Serialize(new
        {
            envelope = new
            {
                system = envelope.SystemPrompt,
                tools = envelope.Tools.Select(t => t.Name),
                messages = envelope.Messages,
            },
            response = rawResponse,
        }),
    };
}
