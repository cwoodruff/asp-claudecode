namespace Intelligently.Core.Agents.Contracts;

/// <summary>Counts the input tokens of an envelope as the model would see it.</summary>
public interface IPromptTokenCounter
{
    Task<int> CountAsync(PromptEnvelope envelope, CancellationToken ct = default);
}
