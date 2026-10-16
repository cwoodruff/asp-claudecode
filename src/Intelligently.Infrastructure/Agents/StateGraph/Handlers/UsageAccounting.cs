using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Agents.Evidence;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>
/// Shared helper for the stages that call the model: charges the usage (tokens and USD) to the run through
/// the cost accountant and records the model-call evidence row, with the cost, under the current span.
/// </summary>
internal static class UsageAccounting
{
    /// <summary>Charges the call to the run and appends its evidence row. Returns the cost of the call.</summary>
    public static async Task<decimal> RecordAsync(
        ICostAccountant costs,
        IEvidenceSink evidence,
        AgentRun run,
        AgentStage stage,
        PromptEnvelope envelope,
        ModelUsage usage,
        string rawResponse,
        string? contentJson,
        ModelCallMetadata? metadata,
        Guid? stepId,
        CancellationToken ct)
    {
        var cost = costs.Charge(run, usage);
        await evidence.RecordAsync(run, TraceWriter.ModelCall(stage, envelope, usage, rawResponse, contentJson, metadata, cost, stepId), ct);
        return cost;
    }
}
