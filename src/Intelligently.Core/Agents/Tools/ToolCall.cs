using System.Text.Json;

namespace Intelligently.Core.Agents.Tools;

/// <summary>A tool invocation proposed by the model.</summary>
public sealed record ToolCall(string Id, string Name, JsonElement Input);
