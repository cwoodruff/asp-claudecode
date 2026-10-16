using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>Context Plane: assembles the prompt envelope for a stage within budget.</summary>
public interface IContextAssembler
{
    Task<PromptEnvelope> BuildAsync(AgentRun run, AgentStage stage, CancellationToken ct = default);
}
