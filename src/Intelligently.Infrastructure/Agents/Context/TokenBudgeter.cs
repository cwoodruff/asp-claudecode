using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.StateGraph;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Context;

/// <summary>
/// Fits a prompt envelope into the stage's input-token budget (<c>Agents:Budgets:PromptTokens</c>,
/// overridable per stage with <c>Agents:Budgets:StagePromptTokens</c>). Counts with the provider before
/// each model call and trims from the lowest-priority block upward: one episodic entry at a time, then the
/// episodic block, then one knowledge entry at a time, then the knowledge block. The frozen system prompt,
/// the tools, the transcript and the stage instruction are never trimmed; a transcript that outgrows the
/// budget is handled by compaction instead.
/// </summary>
public sealed class TokenBudgeter
{
    private static readonly PromptSection[] TrimOrder = [PromptSection.Episodic, PromptSection.Knowledge];

    private readonly IPromptTokenCounter _counter;
    private readonly AgentsOptions _options;
    private readonly ILogger<TokenBudgeter> _logger;

    public TokenBudgeter(IPromptTokenCounter counter, IOptions<AgentsOptions> options, ILogger<TokenBudgeter> logger)
    {
        _counter = counter;
        _options = options.Value;
        _logger = logger;
    }

    public Task<PromptEnvelope> FitAsync(PromptEnvelope envelope, AgentStage stage, CancellationToken ct = default) =>
        FitAsync(envelope, _options.Budgets.PromptTokensFor(stage.ToString()), ct);

    /// <summary>Trims until the counted input tokens are within <paramref name="budget"/>; records the final count.</summary>
    public async Task<PromptEnvelope> FitAsync(PromptEnvelope envelope, int budget, CancellationToken ct = default)
    {
        var count = await _counter.CountAsync(envelope, ct);
        var trims = 0;
        while (count > budget)
        {
            var trimmed = Trim(envelope);
            if (trimmed is null)
            {
                _logger.LogWarning(
                    "Run {RunId}: prompt for {Stage} is {Tokens} tokens, over the {Budget} budget, with nothing left to trim",
                    envelope.RunId, envelope.Stage, count, budget);
                break;
            }

            envelope = trimmed;
            trims++;
            count = await _counter.CountAsync(envelope, ct);
        }

        if (trims > 0)
            _logger.LogInformation("Run {RunId}: trimmed {Trims} block(s) to fit {Stage} prompt into {Budget} tokens ({Tokens} after)", envelope.RunId, trims, envelope.Stage, budget, count);

        return envelope with { InputTokens = count };
    }

    /// <summary>
    /// Removes the lowest-priority trimmable unit: the last entry of the episodic block (or the block itself
    /// when one entry is left), then the same for the knowledge block. Null when nothing is left to trim.
    /// </summary>
    public static PromptEnvelope? Trim(PromptEnvelope envelope)
    {
        foreach (var section in TrimOrder)
        {
            var index = -1;
            for (var i = envelope.Messages.Count - 1; i >= 0; i--)
            {
                if (envelope.Messages[i].Section == section)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0) continue;

            var messages = envelope.Messages.ToList();
            var message = messages[index];
            // Content is a header block followed by one block per entry.
            if (message.Content.Count > 2)
                messages[index] = message with { Content = message.Content.Take(message.Content.Count - 1).ToList() };
            else
                messages.RemoveAt(index);

            return envelope with { Messages = messages };
        }

        return null;
    }
}
