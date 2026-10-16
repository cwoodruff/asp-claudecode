using Intelligently.Core.Agents.Entities;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>Produces the short summary stored on an <see cref="AgentEpisode"/> (local compaction mode).</summary>
public interface IEpisodeSummarizer
{
    Task<string> SummarizeAsync(AgentRun run, string transcript, CancellationToken ct = default);
}
