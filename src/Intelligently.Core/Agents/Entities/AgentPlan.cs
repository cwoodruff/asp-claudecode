using System.Text.Json.Serialization;

namespace Intelligently.Core.Agents.Entities;

/// <summary>
/// A plan produced by the Intelligence Plane. The JSON-visible members form the
/// structured-output contract (the schema is exported from this record); the
/// members marked with <see cref="JsonIgnoreAttribute"/> are persistence metadata.
/// </summary>
public sealed record AgentPlan
{
    [JsonIgnore] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonIgnore] public Guid RunId { get; init; }
    [JsonIgnore] public int Revision { get; init; }

    /// <summary>SHA-256 of the canonical plan JSON, used for oscillation detection.</summary>
    [JsonIgnore] public string Hash { get; init; } = string.Empty;

    [JsonIgnore] public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>One sentence restating the goal of the task.</summary>
    [JsonPropertyName("goal")]
    public required string Goal { get; init; }

    /// <summary>Ordered steps to reach the goal.</summary>
    [JsonPropertyName("steps")]
    public required IReadOnlyList<PlanStep> Steps { get; init; }
}

/// <summary>One step of an <see cref="AgentPlan"/>.</summary>
public sealed record PlanStep
{
    /// <summary>0-based position of the step in the plan.</summary>
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    /// <summary>What the step must achieve.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>Name of the tool to use for this step, or null when the step is reasoning only.</summary>
    [JsonPropertyName("tool")]
    public string? Tool { get; init; }

    /// <summary>How Verify decides the step succeeded.</summary>
    [JsonPropertyName("success_criteria")]
    public required string SuccessCriteria { get; init; }
}
