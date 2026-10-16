using System.Text.Json;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>Interpret: validate the task spec and budgets before any model call.</summary>
public sealed class InterpretHandler : IStageHandler
{
    public AgentStage Stage => AgentStage.Interpret;

    public Task<StageOutcome> ExecuteAsync(AgentRun run, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(run.TaskType))
            return Fail("Task type is empty.");

        if (string.IsNullOrWhiteSpace(run.UserId) || !run.Principal.IsAuthenticated)
            return Fail("Run has no principal; no principal, no tools.");

        JsonDocument spec;
        try
        {
            spec = JsonDocument.Parse(run.TaskSpec);
        }
        catch (JsonException ex)
        {
            return Fail($"Task spec is not valid JSON: {ex.Message}");
        }

        using (spec)
        {
            if (spec.RootElement.ValueKind != JsonValueKind.Object || !spec.RootElement.EnumerateObject().Any())
                return Fail("Task spec must be a non-empty JSON object.");
        }

        if (run.Budget.IsExhausted)
            return Fail("Recovery budget is already exhausted.");

        return Task.FromResult(StageOutcome.To(AgentStage.Plan, "Spec valid, within budget."));
    }

    private static Task<StageOutcome> Fail(string reason) =>
        Task.FromResult(StageOutcome.To(AgentStage.Failed, reason));
}
