using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Entities.AI;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>Outcome of resolving an escalation: the escalation, the knowledge entry when one was published, and the episode it was linked to.</summary>
public sealed record EscalationResolution(AIEscalation Escalation, AIKnowledgeEntry? KnowledgeEntry, AgentEpisode? Episode);

/// <summary>
/// The instructor side of the Learn stage (plan section 3, Phase 5): resolves an <see cref="AIEscalation"/>
/// raised by an agent run and, when the instructor marks the outcome reusable (<c>AddToKnowledgeBase</c>),
/// publishes the answer as an <see cref="AIKnowledgeEntry"/> linked from the run's <see cref="AgentEpisode"/>.
/// </summary>
public interface IEscalationResolver
{
    /// <summary>
    /// Records the instructor's response on the escalation and resolves it. With
    /// <paramref name="addToKnowledgeBase"/> an <see cref="AIKnowledgeEntry"/> is created from the learner
    /// question and the response (source: the escalation) and the run's episode is linked to it; an
    /// escalated run that never reached Learn gets its episode written here. Saves immediately.
    /// </summary>
    Task<EscalationResolution> ResolveAsync(int escalationId, string instructorUserId, string response, bool addToKnowledgeBase, CancellationToken ct = default);
}
