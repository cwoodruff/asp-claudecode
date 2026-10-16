using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>Shared helpers for charging model usage to a run and recording it as evidence.</summary>
internal static class UsageAccounting
{
    /// <summary>
    /// Charges a model call to the run. The provider reports cached reads and cache creation separately
    /// from <c>input_tokens</c>, so all three make up the prompt that was processed and all three count
    /// toward the run's input total and its token budget.
    /// </summary>
    public static void Apply(AgentRun run, ModelUsage usage)
    {
        var input = usage.InputTokens + usage.CacheReadInputTokens + usage.CacheCreationInputTokens;
        run.TotalInputTokens += input;
        run.TotalOutputTokens += usage.OutputTokens;
        run.Budget.TrySpendTokens(input + usage.OutputTokens);
    }

    /// <summary>
    /// The model-call trace: the exact envelope (system blocks, tools, messages with sections and cache
    /// breakpoints), the raw response, and the usage including cache read and creation tokens.
    /// </summary>
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
                system = envelope.System,
                tools = envelope.Tools.Select(t => t.Name),
                messages = envelope.Messages,
                prefixHash = envelope.PrefixHash(),
                promptTokens = envelope.InputTokens,
                compaction = envelope.Compaction,
            },
            response = rawResponse,
        }),
    };
}
