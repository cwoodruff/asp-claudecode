using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;

namespace Intelligently.Infrastructure.Agents.Context;

/// <summary>
/// Offline fallback for the episode summary when the model is unavailable, and the summarizer of the
/// test hosts: the last assistant line of the transcript, else its goal line, else its first characters.
/// </summary>
public sealed class ExtractiveSummarizer : IEpisodeSummarizer
{
    public const string GoalPrefix = "Goal: ";
    public const string AssistantPrefix = "Assistant: ";
    private const int MaxChars = 600;

    public Task<string> SummarizeAsync(AgentRun run, string transcript, CancellationToken ct = default) =>
        Task.FromResult(Summarize(run, transcript));

    public static string Summarize(AgentRun run, string transcript)
    {
        var lines = transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var picked = lines.LastOrDefault(l => l.StartsWith(AssistantPrefix, StringComparison.Ordinal))?[AssistantPrefix.Length..]
                     ?? lines.FirstOrDefault(l => l.StartsWith(GoalPrefix, StringComparison.Ordinal))?[GoalPrefix.Length..]
                     ?? (transcript.Length > 0 ? transcript : $"Run {run.Id} finished.");
        picked = picked.Trim();
        return picked.Length > MaxChars ? picked[..MaxChars] : picked;
    }
}
