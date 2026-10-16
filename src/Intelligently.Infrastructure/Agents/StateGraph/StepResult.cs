using Intelligently.Core.Agents.Entities;

namespace Intelligently.Infrastructure.Agents.StateGraph;

/// <summary>
/// Outcome of one <see cref="StateGraphRunner.TryStepAsync(Guid, string, CancellationToken)"/> call.
/// <paramref name="Executed"/> is false when no stage ran: the run is not Running, or another worker
/// holds an unexpired lease on it. <paramref name="Run"/> is the run as persisted either way.
/// </summary>
public readonly record struct StepResult(AgentRun Run, bool Executed);
