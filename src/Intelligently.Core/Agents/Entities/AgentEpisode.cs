namespace Intelligently.Core.Agents.Entities;

/// <summary>Episodic memory: one row per finished run, written by the Learn stage.</summary>
public sealed class AgentEpisode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int CourseId { get; set; }
    public string TaskType { get; set; } = string.Empty;

    /// <summary>Free text of the task (<see cref="Contracts.TaskText"/>), the basis for similarity lookup.</summary>
    public string TaskText { get; set; } = string.Empty;

    /// <summary>Completed | Failed.</summary>
    public string Outcome { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;

    /// <summary>JSON object of tool name to call count.</summary>
    public string ToolUsageJson { get; set; } = "{}";

    /// <summary>Embedding of <see cref="TaskText"/> for vector similarity; stored as JSON, filled by a later phase.</summary>
    public float[]? Embedding { get; set; }

    public int? KnowledgeEntryId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
