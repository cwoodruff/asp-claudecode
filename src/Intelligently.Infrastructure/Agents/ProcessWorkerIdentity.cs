using Intelligently.Core.Agents.Contracts;

namespace Intelligently.Infrastructure.Agents;

/// <summary>Identifies this process as <c>{machine name}/{process id}</c>.</summary>
public sealed class ProcessWorkerIdentity : IWorkerIdentity
{
    public string Id { get; } = $"{Environment.MachineName}/{Environment.ProcessId}";
}
