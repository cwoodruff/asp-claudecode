using System.Text.Json;
using Anthropic.Core;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Infrastructure.Agents.Context;

namespace Intelligently.Infrastructure.Agents.Intelligence;

/// <summary>
/// Maps a provider-neutral <see cref="PromptEnvelope"/> onto the official SDK's request types without
/// reordering anything: system blocks become <c>system</c> text blocks (cache breakpoints as
/// <c>cache_control</c>), tools become strict tool definitions with <c>cache_control</c> on the last one,
/// and messages keep their roles, so the per-stage instruction travels as a <c>system</c> message inside
/// <c>messages[]</c>. Raw blocks (stored response content) are passed through verbatim.
/// </summary>
public static class PromptEnvelopeMapper
{
    public static List<BetaTextBlockParam>? System(PromptEnvelope envelope) =>
        envelope.System.Count == 0
            ? null
            : envelope.System.Select(b => new BetaTextBlockParam
            {
                Text = b.Text,
                CacheControl = b.CacheBreakpoint ? new BetaCacheControlEphemeral() : null,
            }).ToList();

    public static MessageCreateParamsSystem? CreateSystem(PromptEnvelope envelope) =>
        System(envelope) is { } blocks ? blocks : null;

    public static MessageCountTokensParamsSystem? CountTokensSystem(PromptEnvelope envelope) =>
        System(envelope) is { } blocks ? blocks : null;

    public static List<BetaToolUnion>? Tools(PromptEnvelope envelope) =>
        BetaTools(envelope)?.Select(t => (BetaToolUnion)t).ToList();

    public static List<Tool>? CountTokensTools(PromptEnvelope envelope) =>
        BetaTools(envelope)?.Select(t => (Tool)t).ToList();

    /// <summary>Strict tool definitions in envelope order, with a cache breakpoint on the last one.</summary>
    public static List<BetaTool>? BetaTools(PromptEnvelope envelope)
    {
        if (envelope.Tools.Count == 0) return null;
        var tools = new List<BetaTool>(envelope.Tools.Count);
        for (var i = 0; i < envelope.Tools.Count; i++)
        {
            var descriptor = envelope.Tools[i];
            var last = i == envelope.Tools.Count - 1;
            tools.Add(new BetaTool
            {
                Name = descriptor.Name,
                Description = descriptor.Description,
                InputSchema = InputSchema.FromRawUnchecked(AgentJsonSchema.ToDictionary(descriptor.InputSchema)),
                Strict = true,
                CacheControl = envelope.CacheTools && last ? new BetaCacheControlEphemeral() : null,
            });
        }
        return tools;
    }

    public static List<BetaMessageParam> Messages(PromptEnvelope envelope) =>
        envelope.Messages.Select(Message).ToList();

    public static BetaMessageParam Message(PromptMessage message) => new()
    {
        Role = RoleOf(message.Role),
        Content = message.Content.Select(Block).ToList(),
    };

    public static BetaMessageParam AssistantTurn(IEnumerable<JsonElement> responseBlocks) => new()
    {
        Role = Role.Assistant,
        Content = responseBlocks.Select(b => new BetaContentBlockParam(b)).ToList(),
    };

    public static BetaContextManagementConfig? ContextManagement(PromptEnvelope envelope, AgentsOptions options) =>
        envelope.Compaction ? Compactor.ServerSideConfig(options.Context.CompactAfterTokens) : null;

    public static List<ApiEnum<string, AnthropicBeta>>? Betas(PromptEnvelope envelope) =>
        envelope.Compaction ? [Compactor.BetaHeader] : null;

    private static Role RoleOf(string role) => role.ToLowerInvariant() switch
    {
        PromptMessage.AssistantRole => Role.Assistant,
        PromptMessage.SystemRole => Role.System,
        _ => Role.User,
    };

    private static BetaContentBlockParam Block(PromptContent content)
    {
        var cache = content.CacheBreakpoint ? new BetaCacheControlEphemeral() : null;
        switch (content.Kind)
        {
            case PromptContent.ToolUseKind:
                return new BetaToolUseBlockParam
                {
                    ID = content.ToolUseId ?? string.Empty,
                    Name = content.ToolName ?? string.Empty,
                    Input = content.Input is { ValueKind: JsonValueKind.Object } input
                        ? AgentJsonSchema.ToDictionary(input)
                        : new Dictionary<string, JsonElement>(),
                    CacheControl = cache,
                };

            case PromptContent.ToolResultKind:
                return new BetaToolResultBlockParam
                {
                    ToolUseID = content.ToolUseId ?? string.Empty,
                    Content = content.Text ?? string.Empty,
                    IsError = content.IsError ? true : null,
                    CacheControl = cache,
                };

            case PromptContent.RawKind when content.Raw is { } raw:
                return new BetaContentBlockParam(content.CacheBreakpoint ? WithCacheControl(raw) : raw);

            default:
                return new BetaTextBlockParam { Text = content.Text ?? string.Empty, CacheControl = cache };
        }
    }

    private static JsonElement WithCacheControl(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object) return block;
        var properties = block.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        properties["cache_control"] = JsonSerializer.SerializeToElement(new { type = "ephemeral" });
        return JsonSerializer.SerializeToElement(properties);
    }
}
