namespace Intelligently.Core.Entities.AI;

public class AIAnalyticsEvent
{
    public long Id { get; set; }
    // EventType values: MessageSent | EscalationTriggered | EscalationResolved
    //                   FeedbackPositive | FeedbackNegative | SessionStarted | SessionClosed
    public string EventType { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public int CourseId { get; set; }
    public Guid? ConversationId { get; set; }
    public int? EscalationId { get; set; }
    public string? Metadata { get; set; } // JSON
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
