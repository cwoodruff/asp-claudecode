using System.Text.Json;

namespace Intelligently.Core.Agents.Tools;

/// <summary>Describes a tool the model may call. <see cref="InputSchema"/> is a full JSON Schema object.</summary>
public sealed record ToolDescriptor(
    string Name,
    string Description,
    JsonElement InputSchema,
    ToolRiskLevel RiskLevel);
