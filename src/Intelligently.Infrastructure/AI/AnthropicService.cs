using Anthropic;
using Anthropic.SDK.Messaging;
using Intelligently.Core.Interfaces;
using Polly;
using Polly.Retry;
using System.Runtime.CompilerServices;

namespace Intelligently.Infrastructure.AI;

public sealed class AnthropicService : IAnthropicService
{
    private readonly AnthropicClient _client;
    private readonly AsyncRetryPolicy _retryPolicy;
    private const string Sonnet = "claude-sonnet-4-20250514";
    private const string Haiku  = "claude-haiku-4-20250514";
 
    public AnthropicService(AnthropicClient client)
    {
        _client = client;
        _retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, attempt =>
                TimeSpan.FromSeconds(Math.Pow(2, attempt)));
    }
 
    public async Task<string> GetCompletionAsync(PromptRequest request, CancellationToken ct = default)
    {
        return await _retryPolicy.ExecuteAsync(async () => {
            var msg = await _client.Messages.CreateAsync(new MessageParameters {
                Model = request.Model ?? Haiku,
                MaxTokens = request.MaxOutputTokens,
                System = request.SystemPrompt,
                Messages = BuildMessages(request.Messages)
            }, ct);
            return msg.Content.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty;
        });
    }
 
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        PromptRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in _client.Messages.StreamAsync(new MessageParameters {
                           Model = request.Model ?? Sonnet,
                           MaxTokens = request.MaxOutputTokens,
                           System = request.SystemPrompt,
                           Messages = BuildMessages(request.Messages),
                           Stream = true
                       }, ct))
        {
            if (evt is ContentBlockDeltaEvent { Delta: TextDelta text })
                yield return text.Text;
        }
    }
 
    private static List<Message> BuildMessages(
        IReadOnlyList<(string Role, string Content)> messages) =>
        messages.Select(m => new Message { Role = m.Role, Content = m.Content })
            .ToList();
}