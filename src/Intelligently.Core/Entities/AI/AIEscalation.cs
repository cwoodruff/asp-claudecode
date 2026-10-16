namespace Intelligently.Core.Entities.AI;

public class AIEscalation
{
    public int Id { get; set; }

    /// <summary>Chat conversation that raised the escalation; null when raised by an agent run.</summary>
    public Guid? ConversationId { get; set; }

    /// <summary>Agent run that raised the escalation; null when raised from chat.</summary>
    public Guid? AgentRunId { get; set; }

    public long? MessageId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int CourseId { get; set; }
    public int? ModuleId { get; set; }
    public string LearnerQuestion { get; set; } = string.Empty;
    public string? AIAttemptedResponse { get; set; }
    public string EscalationReason { get; set; } = string.Empty;
    public string Priority { get; set; } = "Medium"; // High | Medium | Low
    public string Status { get; set; } = "New";      // New | InProgress | Resolved | Closed
    public string? InstructorResponse { get; set; }
    public string? InstructorUserId { get; set; }
    public bool AddToKnowledgeBase { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public DateTime? NotifiedInstructorAt { get; set; }
    public DateTime? LearnerNotifiedAt { get; set; }

    public AIConversation? Conversation { get; set; }
}
