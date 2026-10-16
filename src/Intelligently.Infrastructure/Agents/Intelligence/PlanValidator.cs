using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;

namespace Intelligently.Infrastructure.Agents.Intelligence;

/// <summary>
/// The one definition of an acceptable plan: a goal, sequential steps with descriptions, tools that exist
/// in the registry, and the task contract's template constraints. The Plan stage and the candidate search
/// apply it alike, so a candidate is only ranked when it could actually be executed.
/// </summary>
public sealed class PlanValidator
{
    private readonly IToolRegistry _tools;
    private readonly ITaskTypeRegistry _tasks;

    public PlanValidator(IToolRegistry tools, ITaskTypeRegistry tasks)
    {
        _tools = tools;
        _tasks = tasks;
    }

    /// <summary>The first problem with the plan, or null when it is acceptable.</summary>
    public string? Validate(AgentRun run, AgentPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.Goal)) return "Plan has no goal.";
        if (plan.Steps.Count == 0) return "Plan has no steps.";

        var knownTools = _tools.Descriptors.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (step.Index != i) return $"Plan step {i} has index {step.Index}; indices must be sequential from 0.";
            if (string.IsNullOrWhiteSpace(step.Description)) return $"Plan step {i} has no description.";
            if (step.Tool is not null && !knownTools.Contains(step.Tool)) return $"Plan step {i} names unknown tool '{step.Tool}'.";
        }

        return _tasks.Find(run.TaskType)?.ValidatePlan(run, plan);
    }
}
