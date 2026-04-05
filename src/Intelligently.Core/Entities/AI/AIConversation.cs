namespace Intelligently.Core.Entities.AI;

public class AIConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public int CourseId { get; set; }
    public int? CurrentModuleId { get; set; }
    public string Status { get; set; } = "Active"; // Active | Closed | Escalated
    public DateTime SessionStartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SessionEndedAt { get; set; }
    public int TotalTokensUsed { get; set; }
    public int MessageCount { get; set; }
 
    // Navigation
    public ICollection<AIMessage> Messages { get; set; } = new List<AIMessage>();
    public ICollection<AIEscalation> Escalations { get; set; } = new List<AIEscalation>();
}
