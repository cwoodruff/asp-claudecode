namespace Intelligently.Core.Entities.AI;

public class AIMessage
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = string.Empty; // User | Assistant | System
    public string Content { get; set; } = string.Empty;
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public string? ModelUsed { get; set; }
    public string? RetrievedChunkIds { get; set; } // JSON array
    public decimal? ConfidenceScore { get; set; }
    public bool WasEscalated { get; set; }
    public byte? SatisfactionRating { get; set; } // 1=up 0=down
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? ReferencedModuleId { get; set; }
    public int? ReferencedVideoId { get; set; }
 
    public AIConversation Conversation { get; set; } = null!;
}

