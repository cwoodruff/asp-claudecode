using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Exceptions;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Intelligence;

/// <summary>
/// Intelligence Plane on the official Anthropic SDK: structured output for plans and
/// judgements, strict tools with <c>tool_choice: auto</c> for decisions, adaptive thinking throughout.
/// </summary>
public sealed class AnthropicReasoningEngine : IReasoningEngine
{
    private const string RefusalStopReason = "refusal";
    private const string MaxTokensStopReason = "max_tokens";
    private const int MaxOutputTokens = 16_000;

    private static readonly Dictionary<string, JsonElement> PlanSchema = AgentJsonSchema.ForDictionary<AgentPlan>();
    private static readonly Dictionary<string, JsonElement> JudgementSchema = AgentJsonSchema.ForDictionary<Judgement>();

    private readonly IAnthropicClient _client;
    private readonly AgentsOptions _options;

    public AnthropicReasoningEngine(IAnthropicClient client, IOptions<AgentsOptions> options)
    {
        _client = client;
        _options = options.Value;
    }

    public async Task<PlanResult> PlanAsync(PromptEnvelope envelope, CancellationToken ct = default)
    {
        var parameters = BuildParams(envelope, new OutputConfig
        {
            Effort = Effort.High,
            Format = new JsonOutputFormat { Schema = PlanSchema },
        });

        var message = await _client.Messages.Create(parameters, ct);
        ThrowIfRefused(message);

        var text = TextOf(message);
        var usage = UsageOf(message);
        var raw = message.ToString() ?? text;

        if (message.StopReason == MaxTokensStopReason)
            return new PlanResult(null, usage, raw, "Planner output was truncated at max_tokens.");

        try
        {
            var plan = JsonSerializer.Deserialize<AgentPlan>(text, AgentJsonSchema.SerializerOptions);
            return plan is null
                ? new PlanResult(null, usage, raw, "Planner returned an empty plan.")
                : new PlanResult(plan, usage, raw);
        }
        catch (JsonException ex)
        {
            return new PlanResult(null, usage, raw, $"Planner output did not match the plan schema: {ex.Message}");
        }
    }

    public async Task<DecisionResult> DecideAsync(PromptEnvelope envelope, CancellationToken ct = default)
    {
        var parameters = BuildParams(
            envelope,
            new OutputConfig { Effort = Effort.Medium },
            tools: envelope.Tools.Select(ToSdkTool).Select(t => (ToolUnion)t).ToList(),
            toolChoice: new ToolChoiceAuto());

        var message = await _client.Messages.Create(parameters, ct);
        ThrowIfRefused(message);

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
            UsageOf(message),
            message.ToString() ?? string.Empty);
    }

    public async Task<JudgementResult> JudgeAsync(PromptEnvelope envelope, string rubric, CancellationToken ct = default)
    {
        var messages = envelope.Messages
            .Append(new PromptMessage("user", $"Judge the work above against this rubric and respond with JSON only.\nRubric:\n{rubric}"))
            .ToList();
        var parameters = BuildParams(envelope with { Messages = messages }, new OutputConfig
        {
            Effort = Effort.High,
            Format = new JsonOutputFormat { Schema = JudgementSchema },
        });

        var message = await _client.Messages.Create(parameters, ct);
        ThrowIfRefused(message);

        var text = TextOf(message);
        var judgement = JsonSerializer.Deserialize<Judgement>(text, AgentJsonSchema.SerializerOptions)
                        ?? new Judgement { Passed = false, Score = 0, Rationale = "Judge returned no verdict." };
        return new JudgementResult(judgement.Passed, judgement.Score, judgement.Rationale, UsageOf(message), message.ToString() ?? text);
    }

    private MessageCreateParams BuildParams(
        PromptEnvelope envelope,
        OutputConfig outputConfig,
        List<ToolUnion>? tools = null,
        ToolChoice? toolChoice = null) => new()
    {
        OutputConfig = outputConfig,
        Tools = tools,
        ToolChoice = toolChoice,
        Model = _options.DefaultModel,
        MaxTokens = MaxOutputTokens,
        System = string.IsNullOrWhiteSpace(envelope.SystemPrompt) ? null : (MessageCreateParamsSystem)envelope.SystemPrompt,
        Messages = envelope.Messages.Select(m => new MessageParam
        {
            Role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? Role.Assistant : Role.User,
            Content = m.Content,
        }).ToList(),
        Thinking = new ThinkingConfigAdaptive(),
    };

    private static Tool ToSdkTool(ToolDescriptor descriptor) => new()
    {
        Name = descriptor.Name,
        Description = descriptor.Description,
        InputSchema = InputSchema.FromRawUnchecked(AgentJsonSchema.ToDictionary(descriptor.InputSchema)),
        Strict = true,
    };

    private static void ThrowIfRefused(Message message)
    {
        if (message.StopReason == RefusalStopReason)
        {
            var details = message.StopDetails;
            throw new AIRefusalException(details?.Category, details?.Explanation);
        }
    }

    private static string TextOf(Message message) =>
        string.Concat(message.Content.Select(b => b.Value).OfType<TextBlock>().Select(b => b.Text));

    private static ModelUsage UsageOf(Message message) => new(
        message.Model.Raw(),
        (int)message.Usage.InputTokens,
        (int)message.Usage.OutputTokens,
        (int)(message.Usage.CacheReadInputTokens ?? 0),
        (int)(message.Usage.CacheCreationInputTokens ?? 0));

    /// <summary>Structured-output contract for <see cref="JudgeAsync"/>.</summary>
    public sealed record Judgement
    {
        public required bool Passed { get; init; }
        public required double Score { get; init; }
        public required string Rationale { get; init; }
    }
}
