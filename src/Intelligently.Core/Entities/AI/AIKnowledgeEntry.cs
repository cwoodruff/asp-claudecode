namespace Intelligently.Core.Entities.AI;

public class AIKnowledgeEntry
{
    public int Id { get; set; }
    public int? CourseId { get; set; }
    public int? ModuleId { get; set; }
    public string EntryType { get; set; } = "FAQ"; // FAQ|Misconception|LabError|Clarification|BestPractice
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public string? Tags { get; set; }
    public bool IsActive { get; set; } = true;
    public string AuthoredByUserId { get; set; } = string.Empty;
    public int? SourceEscalationId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? IndexedAt { get; set; }
}
