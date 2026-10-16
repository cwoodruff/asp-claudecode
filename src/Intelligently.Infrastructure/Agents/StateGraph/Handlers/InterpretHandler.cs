using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;

namespace Intelligently.Infrastructure.Agents.StateGraph.Handlers;

/// <summary>
/// Interpret: validate the task spec and budgets before any model call. A task type with a registered
/// contract is validated against its schema and rules (a malformed spec fails the run with the reasons);
/// a task type without one only needs a non-empty JSON object.
/// </summary>
public sealed class InterpretHandler : IStageHandler
{
    private readonly ITaskTypeRegistry _tasks;

    public InterpretHandler(ITaskTypeRegistry tasks)
    {
        _tasks = tasks;
    }

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

        var contract = _tasks.Find(run.TaskType);
        if (contract is not null)
        {
            var validation = contract.ValidateSpec(run);
            if (!validation.IsValid)
                return Fail($"Task spec for {contract.TaskType} is invalid: {validation.Summary}");
        }

        if (run.Budget.IsExhausted)
            return Fail("Recovery budget is already exhausted.");

        var typed = contract is null ? "untyped task" : $"{contract.TaskType} spec valid";
        return Task.FromResult(StageOutcome.To(AgentStage.Plan, $"Spec valid ({typed}), within budget."));
    }

    private static Task<StageOutcome> Fail(string reason) =>
        Task.FromResult(StageOutcome.To(AgentStage.Failed, reason));
}
