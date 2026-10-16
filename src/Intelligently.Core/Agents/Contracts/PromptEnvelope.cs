using Intelligently.Core.Agents.Tools;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>A prompt message; Role is <c>user</c> or <c>assistant</c>.</summary>
public sealed record PromptMessage(string Role, string Content);

/// <summary>
/// Everything the Context Plane hands to the Intelligence Plane for one model call,
/// assembled stable-first so the prefix cache holds.
/// </summary>
public sealed record PromptEnvelope(
    string SystemPrompt,
    IReadOnlyList<ToolDescriptor> Tools,
    IReadOnlyList<PromptMessage> Messages);
