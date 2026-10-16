using Anthropic;
using Anthropic.Models.Beta.Messages;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Infrastructure.Agents.Intelligence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Context;

/// <summary>
/// Counts envelope tokens with <c>client.Messages.CountTokens</c>, mapping the envelope exactly as the
/// reasoning engine sends it. Falls back to an offline estimate when the provider cannot be reached, so a
/// counting outage never stops a run.
/// </summary>
public sealed class AnthropicTokenCounter : IPromptTokenCounter
{
    private readonly IAnthropicClient _client;
    private readonly AgentsOptions _options;
    private readonly ILogger<AnthropicTokenCounter> _logger;

    public AnthropicTokenCounter(IAnthropicClient client, IOptions<AgentsOptions> options, ILogger<AnthropicTokenCounter> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> CountAsync(PromptEnvelope envelope, CancellationToken ct = default)
    {
        try
        {
            var parameters = new MessageCountTokensParams
            {
                Model = _options.DefaultModel,
                Messages = PromptEnvelopeMapper.Messages(envelope),
                Thinking = new BetaThinkingConfigAdaptive(),
            };
            if (PromptEnvelopeMapper.CountTokensSystem(envelope) is { } system)
                parameters = new MessageCountTokensParams(parameters) { System = system };
            if (PromptEnvelopeMapper.CountTokensTools(envelope) is { } tools)
                parameters = new MessageCountTokensParams(parameters) { Tools = tools };
            if (envelope.Compaction)
            {
                parameters = new MessageCountTokensParams(parameters)
                {
                    ContextManagement = PromptEnvelopeMapper.ContextManagement(envelope, _options),
                    Betas = PromptEnvelopeMapper.Betas(envelope),
                };
            }
            var result = await _client.Beta.Messages.CountTokens(parameters, ct);
            return (int)Math.Min(int.MaxValue, result.InputTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var estimate = EstimatingTokenCounter.Estimate(envelope);
            _logger.LogWarning(ex, "Run {RunId}: token counting failed; using an estimate of {Estimate} tokens", envelope.RunId, estimate);
            return estimate;
        }
    }
}
