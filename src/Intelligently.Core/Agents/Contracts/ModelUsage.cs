namespace Intelligently.Core.Agents.Contracts;

/// <summary>Token usage reported by a model call.</summary>
public sealed record ModelUsage(
    string Model,
    int InputTokens,
    int OutputTokens,
    int CacheReadInputTokens = 0,
    int CacheCreationInputTokens = 0)
{
    public static ModelUsage None(string model) => new(model, 0, 0);
}
