using Intelligently.Core.Agents.Contracts;

namespace Intelligently.Infrastructure.Agents.Tasks;

/// <summary>
/// The registered task contracts keyed by task type (case-insensitive). Interpret validates the spec
/// through it, Plan reads the template and plan constraints, Verify the deterministic assertions and
/// rubric, the Control Plane the protected-material rule and Learn the learner-facing output. A task
/// type without a contract runs untyped, as every task type did before Phase 6.
/// </summary>
public sealed class TaskTypeRegistry : ITaskTypeRegistry
{
    private readonly Dictionary<string, ITaskContract> _contracts;

    public TaskTypeRegistry(IEnumerable<ITaskContract> contracts)
    {
        _contracts = new Dictionary<string, ITaskContract>(StringComparer.OrdinalIgnoreCase);
        foreach (var contract in contracts)
        {
            if (string.IsNullOrWhiteSpace(contract.TaskType))
                throw new ArgumentException($"Task contract {contract.GetType().Name} has no task type.", nameof(contracts));
            if (!_contracts.TryAdd(contract.TaskType, contract))
                throw new ArgumentException($"Task type '{contract.TaskType}' is registered twice.", nameof(contracts));
        }
    }

    public IReadOnlyList<ITaskContract> Contracts => _contracts.Values.OrderBy(c => c.TaskType, StringComparer.Ordinal).ToList();

    public ITaskContract? Find(string? taskType) =>
        !string.IsNullOrWhiteSpace(taskType) && _contracts.TryGetValue(taskType.Trim(), out var contract) ? contract : null;
}
