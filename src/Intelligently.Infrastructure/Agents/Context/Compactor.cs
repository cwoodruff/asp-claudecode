using Anthropic;
using Anthropic.Models.Beta.Messages;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Context;

/// <summary>
/// Compaction in two modes (plan section 1.2):
/// <list type="bullet">
///   <item>server-side compaction for long runs: the <c>compact-2026-01-12</c> beta with an input-token
///   trigger, requested by the assembler once the counted prompt passes
///   <c>Agents:Context:CompactAfterTokens</c>; the reasoning engine appends the full response content back
///   on every turn so compaction blocks survive;</item>
///   <item>a local Haiku summary (low effort) used when storing episodes.</item>
/// </list>
/// </summary>
public sealed class Compactor : IEpisodeSummarizer
{
    public const string BetaHeader = "compact-2026-01-12";
    private const string RefusalStopReason = "refusal";
    private const int SummaryMaxTokens = 512;

    private const string SummarySystemPrompt =
        "You write short episodic-memory summaries of autonomous tutoring-agent runs. In two or three " +
        "sentences, state what the learner needed, what the agent did (naming the tools it used), and the " +
        "outcome. Plain text, no preamble, no markdown.";

    private readonly IAnthropicClient _client;
    private readonly AgentsOptions _options;
    private readonly ILogger<Compactor> _logger;

    public Compactor(IAnthropicClient client, IOptions<AgentsOptions> options, ILogger<Compactor> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>True when a prompt of <paramref name="promptTokens"/> input tokens should ask for server-side compaction.</summary>
    public bool ShouldCompact(int promptTokens) => promptTokens >= _options.Context.CompactAfterTokens;

    /// <summary>The compaction trigger sent to the API, in input tokens.</summary>
    public long TriggerTokens => _options.Context.CompactAfterTokens;

    /// <summary>The context-management edit that turns on server-side compaction at <paramref name="triggerTokens"/>.</summary>
    public static BetaContextManagementConfig ServerSideConfig(long triggerTokens) => new()
    {
        Edits = [new BetaCompact20260112Edit { Trigger = new BetaInputTokensTrigger(triggerTokens) }],
    };

    /// <summary>Local mode: a short summary from the cheap model at low effort, with an offline fallback.</summary>
    public async Task<string> SummarizeAsync(AgentRun run, string transcript, CancellationToken ct = default)
    {
        var max = _options.Context.SummaryInputMaxChars;
        var input = transcript.Length > max ? transcript[..max] : transcript;

        try
        {
            var message = await _client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = _options.CheapModel,
                MaxTokens = SummaryMaxTokens,
                System = SummarySystemPrompt,
                OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
                Messages =
                [
                    new BetaMessageParam
                    {
                        Role = Role.User,
                        Content = $"Task type: {run.TaskType}\nCourse id: {run.CourseId}\n\n{input}",
                    },
                ],
            }, ct);

            if (message.StopReason == RefusalStopReason)
            {
                _logger.LogWarning("Run {RunId}: episode summary was refused; using the extractive fallback", run.Id);
                return ExtractiveSummarizer.Summarize(run, transcript);
            }

            var text = string.Concat(message.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(b => b.Text)).Trim();
            return text.Length == 0 ? ExtractiveSummarizer.Summarize(run, transcript) : text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Run {RunId}: episode summary failed; using the extractive fallback", run.Id);
            return ExtractiveSummarizer.Summarize(run, transcript);
        }
    }
}
