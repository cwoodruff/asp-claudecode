namespace Intelligently.Core.Agents.Contracts;

/// <summary>
/// Names the worker instance that claims runs. The id is recorded as <c>AgentRun.ClaimedBy</c>
/// while a stage executes, so operators can see which instance holds a run.
/// </summary>
public interface IWorkerIdentity
{
    /// <summary>Stable for the life of the process, e.g. <c>HOST/12345</c> (machine name plus process id).</summary>
    string Id { get; }
}
