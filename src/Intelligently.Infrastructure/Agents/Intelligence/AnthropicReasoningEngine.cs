using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Exceptions;
using Intelligently.Infrastructure.Agents.Context;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Intelligence;

/// <summary>
/// Intelligence Plane on the official Anthropic SDK: structured output for plans and judgements, strict
/// tools with <c>tool_choice: auto</c> for decisions, adaptive thinking throughout. The envelope is sent as
/// assembled by the Context Plane (prefix, cached blocks and message order untouched); the tool list is
/// sent on every call so the cached prefix is the same for every stage, with <c>tool_choice: none</c> where
/// the call must answer in text. When the envelope asks for server-side compaction the beta header and
/// context-management edit are added, and a <c>compaction</c> stop continues the turn with the full
/// response content appended, so compaction blocks survive. Thinking is requested with
/// <c>display: summarized</c> so the Evidence Plane can store the provider's reasoning summary as text,
/// together with the stop reason, effort and latency of every call.
/// </summary>
public sealed class AnthropicReasoningEngine : IReasoningEngine
{
    private const string RefusalStopReason = "refusal";
    private const string MaxTokensStopReason = "max_tokens";
    private const string CompactionStopReason = "compaction";
    private const int MaxOutputTokens = 16_000;
    private const int MaxCompactionContinuations = 2;

    private static readonly Dictionary<string, JsonElement> PlanSchema = AgentJsonSchema.ForDictionary<AgentPlan>();
    private static readonly Dictionary<string, JsonElement> JudgementSchema = AgentJsonSchema.ForDictionary<Judgement>();

    private readonly IAnthropicClient _client;
    private readonly AgentsOptions _options;
    private readonly CacheHealthMonitor? _cacheHealth;

    public AnthropicReasoningEngine(IAnthropicClient client, IOptions<AgentsOptions> options, CacheHealthMonitor? cacheHealth = null)
    {
        _client = client;
        _options = options.Value;
        _cacheHealth = cacheHealth;
    }

    public async Task<PlanResult> PlanAsync(PromptEnvelope envelope, CancellationToken ct = default)
    {
        var outputConfig = new BetaOutputConfig
        {
            Effort = Effort.High,
            Format = new BetaJsonOutputFormat { Schema = PlanSchema },
        };

        var call = await CallAsync(envelope, messages => BuildParams(envelope, messages, outputConfig, new BetaToolChoiceNone()), "high", ct);
        var message = call.Message;

        var text = TextOf(message);
        var raw = message.ToString() ?? text;

        if (message.StopReason == MaxTokensStopReason)
            return new PlanResult(null, call.Usage, raw, "Planner output was truncated at max_tokens.", call.ContentJson, call.Metadata);

        try
        {
            var plan = JsonSerializer.Deserialize<AgentPlan>(text, AgentJsonSchema.SerializerOptions);
            return plan is null
                ? new PlanResult(null, call.Usage, raw, "Planner returned an empty plan.", call.ContentJson, call.Metadata)
                : new PlanResult(plan, call.Usage, raw, ContentJson: call.ContentJson, Metadata: call.Metadata);
        }
        catch (JsonException ex)
        {
            return new PlanResult(null, call.Usage, raw, $"Planner output did not match the plan schema: {ex.Message}", call.ContentJson, call.Metadata);
        }
    }

    public async Task<DecisionResult> DecideAsync(PromptEnvelope envelope, CancellationToken ct = default)
    {
        var outputConfig = new BetaOutputConfig { Effort = Effort.Medium };
        var call = await CallAsync(envelope, messages => BuildParams(envelope, messages, outputConfig, new BetaToolChoiceAuto()), "medium", ct);
        var message = call.Message;

        var calls = new List<ToolCall>();
        var text = new List<string>();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var textBlock))
                text.Add(textBlock.Text);
            else if (block.TryPickToolUse(out var toolUse))
                calls.Add(new ToolCall(toolUse.ID, toolUse.Name, JsonSerializer.SerializeToElement(toolUse.Input)));
        }

        return new DecisionResult(
            calls,
            text.Count == 0 ? null : string.Concat(text),
            message.StopReason?.Raw() ?? "unknown",
            call.Usage,
            message.ToString() ?? string.Empty,
            call.ContentJson,
            call.Metadata);
    }

    public async Task<JudgementResult> JudgeAsync(PromptEnvelope envelope, string rubric, CancellationToken ct = default)
    {
        // The rubric is a user turn; it goes before the trailing stage instruction so that system message stays last.
        var rubricMessage = new PromptMessage(PromptMessage.UserRole, $"Judge the work above against this rubric and respond with JSON only.\nRubric:\n{rubric}");
        var messages = envelope.Messages.ToList();
        if (messages.Count > 0 && messages[^1].IsSystem)
            messages.Insert(messages.Count - 1, rubricMessage);
        else
            messages.Add(rubricMessage);
        var judged = envelope with { Messages = messages };

        var outputConfig = new BetaOutputConfig
        {
            Effort = Effort.High,
            Format = new BetaJsonOutputFormat { Schema = JudgementSchema },
        };
        var call = await CallAsync(judged, msgs => BuildParams(judged, msgs, outputConfig, new BetaToolChoiceNone()), "high", ct);
        var message = call.Message;

        var text = TextOf(message);
        var judgement = JsonSerializer.Deserialize<Judgement>(text, AgentJsonSchema.SerializerOptions)
                        ?? new Judgement { Passed = false, Score = 0, Rationale = "Judge returned no verdict." };
        return new JudgementResult(judgement.Passed, judgement.Score, judgement.Rationale, call.Usage, message.ToString() ?? text, call.Metadata);
    }

    private sealed record ModelCall(BetaMessage Message, ModelUsage Usage, string ContentJson, ModelCallMetadata Metadata);

    /// <summary>
    /// One model turn. On a <c>compaction</c> stop the full response content is appended as an assistant
    /// turn and the call is repeated, so the compaction block is what the model continues from. Returns the
    /// final message, the summed usage, the JSON array of every response block in order, and the call
    /// metadata (reasoning summary across continuations, final stop reason, effort, total latency).
    /// </summary>
    private async Task<ModelCall> CallAsync(
        PromptEnvelope envelope,
        Func<List<BetaMessageParam>, MessageCreateParams> build,
        string effort,
        CancellationToken ct)
    {
        var messages = PromptEnvelopeMapper.Messages(envelope);
        var blocks = new List<JsonElement>();
        var reasoning = new StringBuilder();
        var prefixHash = envelope.PrefixHash();
        ModelUsage? total = null;
        var stopwatch = Stopwatch.StartNew();

        for (var continuation = 0; ; continuation++)
        {
            var message = await _client.Beta.Messages.Create(build(messages), ct);
            ThrowIfRefused(message);

            var usage = UsageOf(message);
            _cacheHealth?.Observe(envelope.RunId, prefixHash, usage, envelope.Stage);
            total = total is null ? usage : Sum(total, usage);

            var responseBlocks = message.Content.Select(b => b.Json.Clone()).ToList();
            blocks.AddRange(responseBlocks);
            AppendReasoning(reasoning, message);

            if (message.StopReason == CompactionStopReason && continuation < MaxCompactionContinuations)
            {
                messages = [.. messages, PromptEnvelopeMapper.AssistantTurn(responseBlocks)];
                continue;
            }

            stopwatch.Stop();
            var metadata = new ModelCallMetadata(
                reasoning.Length == 0 ? null : reasoning.ToString(),
                message.StopReason?.Raw(),
                effort,
                stopwatch.Elapsed.TotalMilliseconds);
            return new ModelCall(message, total, JsonSerializer.Serialize(blocks), metadata);
        }
    }

    private MessageCreateParams BuildParams(
        PromptEnvelope envelope,
        List<BetaMessageParam> messages,
        BetaOutputConfig outputConfig,
        BetaToolChoice toolChoice)
    {
        var parameters = new MessageCreateParams
        {
            Model = _options.DefaultModel,
            MaxTokens = MaxOutputTokens,
            Messages = messages,
            OutputConfig = outputConfig,
            // Summarized display returns a readable summary of the reasoning (the raw chain of thought is
            // never exposed); the Evidence Plane stores it as text. Thinking is billed the same either way.
            Thinking = new BetaThinkingConfigAdaptive { Display = Display.Summarized },
        };
        // Optional fields are layered on through the copy constructor so absent ones are never serialized.
        if (PromptEnvelopeMapper.CreateSystem(envelope) is { } system)
            parameters = new MessageCreateParams(parameters) { System = system };
        if (PromptEnvelopeMapper.Tools(envelope) is { } tools)
            parameters = new MessageCreateParams(parameters) { Tools = tools, ToolChoice = toolChoice };
        if (envelope.Compaction)
        {
            parameters = new MessageCreateParams(parameters)
            {
                ContextManagement = PromptEnvelopeMapper.ContextManagement(envelope, _options),
                Betas = PromptEnvelopeMapper.Betas(envelope),
            };
        }
        return parameters;
    }

    private static void ThrowIfRefused(BetaMessage message)
    {
        if (message.StopReason == RefusalStopReason)
        {
            var details = message.StopDetails;
            throw new AIRefusalException(details?.Category, details?.Explanation);
        }
    }

    /// <summary>The text of the response's thinking blocks (the summary when <c>display: summarized</c>), one paragraph per block.</summary>
    private static void AppendReasoning(StringBuilder reasoning, BetaMessage message)
    {
        foreach (var block in message.Content)
        {
            if (!block.TryPickThinking(out var thinking) || string.IsNullOrWhiteSpace(thinking.Thinking)) continue;
            if (reasoning.Length > 0) reasoning.AppendLine().AppendLine();
            reasoning.Append(thinking.Thinking.Trim());
        }
    }

    private static string TextOf(BetaMessage message) =>
        string.Concat(message.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(b => b.Text));

    private static ModelUsage UsageOf(BetaMessage message) => new(
        message.Model.Raw(),
        (int)message.Usage.InputTokens,
        (int)message.Usage.OutputTokens,
        (int)(message.Usage.CacheReadInputTokens ?? 0),
        (int)(message.Usage.CacheCreationInputTokens ?? 0));

    private static ModelUsage Sum(ModelUsage a, ModelUsage b) => new(
        a.Model,
        a.InputTokens + b.InputTokens,
        a.OutputTokens + b.OutputTokens,
        a.CacheReadInputTokens + b.CacheReadInputTokens,
        a.CacheCreationInputTokens + b.CacheCreationInputTokens);

    /// <summary>Structured-output contract for <see cref="JudgeAsync"/>.</summary>
    public sealed record Judgement
    {
        public required bool Passed { get; init; }
        public required double Score { get; init; }
        public required string Rationale { get; init; }
    }
}
