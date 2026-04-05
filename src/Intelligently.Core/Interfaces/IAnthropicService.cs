namespace Intelligently.Core.Interfaces;

public record PromptRequest(
    string SystemPrompt,
    IReadOnlyList<(string Role, string Content)> Messages,
    string? Model = null,
    int MaxOutputTokens = 1024
);
 
public interface IAnthropicService
{
    Task<string> GetCompletionAsync(PromptRequest request, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamCompletionAsync(PromptRequest request, CancellationToken ct = default);
}

