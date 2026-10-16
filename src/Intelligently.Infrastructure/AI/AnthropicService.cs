using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Messages;
using Intelligently.Core.Exceptions;
using Intelligently.Core.Interfaces;

namespace Intelligently.Infrastructure.AI;

/// <summary>
/// <see cref="IAnthropicService"/> implemented on the official Anthropic SDK.
/// The SDK already retries 408/409/429/5xx and connection errors with backoff,
/// so no additional retry wrapper is applied here.
/// </summary>
public sealed class AnthropicService : IAnthropicService
{
    /// <summary>Default model for both completion and streaming paths.</summary>
    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>Cheap, fast model for routing, classification and other low-stakes calls.</summary>
    public const string CheapModel = "claude-haiku-5-5";

    private const string RefusalStopReason = "refusal";

    private readonly IAnthropicClient _client;

    public AnthropicService(IAnthropicClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<string> GetCompletionAsync(PromptRequest request, CancellationToken ct = default)
    {
        var message = await _client.Messages.Create(BuildParams(request), ct);

        if (message.StopReason == RefusalStopReason)
        {
            var details = message.StopDetails;
            throw new AIRefusalException(details?.Category, details?.Explanation);
        }

        return string.Concat(message.Content
            .Select(block => block.Value)
            .OfType<TextBlock>()
            .Select(block => block.Text));
    }

    public async IAsyncEnumerable<string> StreamCompletionAsync(
        PromptRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in _client.Messages.CreateStreaming(BuildParams(request), ct))
        {
            if (evt.TryPickContentBlockDelta(out var blockDelta) &&
                blockDelta.Delta.TryPickText(out var text))
            {
                yield return text.Text;
            }
            else if (evt.TryPickDelta(out var messageDelta) &&
                     messageDelta.Delta.StopReason == RefusalStopReason)
            {
                var details = messageDelta.Delta.StopDetails;
                throw new AIRefusalException(details?.Category, details?.Explanation);
            }
        }
    }

    private static MessageCreateParams BuildParams(PromptRequest request)
    {
        return new MessageCreateParams
        {
            Model = request.Model ?? DefaultModel,
            MaxTokens = request.MaxOutputTokens,
            System = string.IsNullOrWhiteSpace(request.SystemPrompt)
                ? null
                : (MessageCreateParamsSystem)request.SystemPrompt,
            Messages = request.Messages.Select(ToMessageParam).ToList(),
            Thinking = new ThinkingConfigAdaptive(),
        };
    }

    private static MessageParam ToMessageParam((string Role, string Content) message) => new()
    {
        Role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? Role.Assistant
            : Role.User,
        Content = message.Content,
    };
}
