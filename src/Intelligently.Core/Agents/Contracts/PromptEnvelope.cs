using System.Text;
using System.Text.Json;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>
/// Which part of the stable-to-volatile envelope a message belongs to (plan section 1.2).
/// The order of the enum is the order in the prompt; the token budgeter trims from the
/// lowest priority upward: <see cref="Episodic"/> first, then <see cref="Knowledge"/>.
/// </summary>
public enum PromptSection
{
    /// <summary>Retrieved knowledge entries for the course/module.</summary>
    Knowledge,
    /// <summary>Episodic summary of prior runs for this learner and task type.</summary>
    Episodic,
    /// <summary>The append-only run transcript (task, plans, steps, results).</summary>
    Transcript,
    /// <summary>The volatile per-stage instruction (a mid-conversation system message).</summary>
    Instruction,
}

/// <summary>
/// One content block of a prompt message. <see cref="Kind"/> is <c>text</c>, <c>tool_use</c>,
/// <c>tool_result</c> or <c>raw</c>. A raw block is a provider content block (thinking, compaction,
/// tool use) stored verbatim from an earlier response and replayed unchanged, so signed blocks survive.
/// </summary>
public sealed record PromptContent
{
    public const string TextKind = "text";
    public const string ToolUseKind = "tool_use";
    public const string ToolResultKind = "tool_result";
    public const string RawKind = "raw";

    public required string Kind { get; init; }
    public string? Text { get; init; }
    public string? ToolUseId { get; init; }
    public string? ToolName { get; init; }
    public JsonElement? Input { get; init; }
    public bool IsError { get; init; }
    public JsonElement? Raw { get; init; }

    /// <summary>Marks a prompt-cache breakpoint on this block.</summary>
    public bool CacheBreakpoint { get; init; }

    public static PromptContent FromText(string text) => new() { Kind = TextKind, Text = text };

    public static PromptContent ToolUse(ToolCall call) => new()
    {
        Kind = ToolUseKind,
        ToolUseId = call.Id,
        ToolName = call.Name,
        Input = call.Input,
    };

    public static PromptContent ToolResult(ToolResult result) => new()
    {
        Kind = ToolResultKind,
        ToolUseId = result.ToolCallId,
        ToolName = result.Name,
        Text = result.Content,
        IsError = result.IsError,
    };

    public static PromptContent FromRaw(JsonElement block) => new() { Kind = RawKind, Raw = block.Clone() };
}

/// <summary>A prompt message; Role is <c>user</c>, <c>assistant</c> or <c>system</c> (mid-conversation instruction).</summary>
public sealed record PromptMessage(string Role, IReadOnlyList<PromptContent> Content, PromptSection Section = PromptSection.Transcript)
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";
    public const string SystemRole = "system";

    public PromptMessage(string role, string text, PromptSection section = PromptSection.Transcript)
        : this(role, [PromptContent.FromText(text)], section) { }

    /// <summary>The concatenated text blocks of the message.</summary>
    public string Text => string.Concat(Content.Where(c => c.Kind == PromptContent.TextKind).Select(c => c.Text));

    public bool IsSystem => string.Equals(Role, SystemRole, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One block of the frozen system prompt.</summary>
public sealed record PromptBlock(string Text, bool CacheBreakpoint = false);

/// <summary>
/// Everything the Context Plane hands to the Intelligence Plane for one model call, assembled in the
/// fixed stable-to-volatile order of plan section 1.2: frozen system blocks (cache breakpoint), the
/// deterministic sorted tool list (cache breakpoint), then messages in section order ending with the
/// per-stage instruction as a <c>system</c> message inside <see cref="Messages"/>, never in the top-level
/// system field, so the cached prefix survives.
/// </summary>
public sealed record PromptEnvelope(
    IReadOnlyList<PromptBlock> System,
    IReadOnlyList<ToolDescriptor> Tools,
    IReadOnlyList<PromptMessage> Messages)
{
    public PromptEnvelope(string systemPrompt, IReadOnlyList<ToolDescriptor> tools, IReadOnlyList<PromptMessage> messages)
        : this([new PromptBlock(systemPrompt, CacheBreakpoint: true)], tools, messages) { }

    /// <summary>The system blocks joined, for evidence and logging.</summary>
    public string SystemPrompt => string.Join("\n\n", System.Select(b => b.Text));

    /// <summary>Run the envelope was built for, when known (used for cache-health tracking).</summary>
    public Guid? RunId { get; init; }

    public AgentStage? Stage { get; init; }

    /// <summary>Input token count measured by the budgeter after trimming, when it ran.</summary>
    public int? InputTokens { get; init; }

    /// <summary>Marks a cache breakpoint on the last tool definition.</summary>
    public bool CacheTools { get; init; } = true;

    /// <summary>Ask for server-side compaction of the transcript on this call (long runs).</summary>
    public bool Compaction { get; init; }

    /// <summary>
    /// The stable prefix (system blocks and tools) serialized deterministically. Two envelopes with the
    /// same prefix bytes share a prompt-cache prefix.
    /// </summary>
    public string SerializePrefix()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("system");
            foreach (var block in System)
            {
                writer.WriteStartObject();
                writer.WriteString("text", block.Text);
                writer.WriteBoolean("cache", block.CacheBreakpoint);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("tools");
            foreach (var tool in Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("input_schema");
                CanonicalJson.Write(writer, tool.InputSchema);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>SHA-256 hex of <see cref="SerializePrefix"/>.</summary>
    public string PrefixHash() =>
        Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(SerializePrefix())));
}
