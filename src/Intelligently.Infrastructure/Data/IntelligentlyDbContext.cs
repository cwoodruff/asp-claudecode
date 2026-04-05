using Intelligently.Core.Entities.AI;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Infrastructure.Data;

public class IntelligentlyDbContext : IdentityDbContext
{
    public IntelligentlyDbContext(DbContextOptions<IntelligentlyDbContext> options) : base(options) { }
 
    public DbSet<AIConversation>    AIConversations    => Set<AIConversation>();
    public DbSet<AIMessage>         AIMessages         => Set<AIMessage>();
    public DbSet<AIEscalation>      AIEscalations      => Set<AIEscalation>();
    public DbSet<AIKnowledgeEntry>  AIKnowledgeEntries => Set<AIKnowledgeEntry>();
    public DbSet<AIAnalyticsEvent>  AIAnalyticsEvents  => Set<AIAnalyticsEvent>();
 
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
 
        b.Entity<AIConversation>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("Active");
            e.HasIndex(x => new { x.UserId, x.CourseId, x.Status });
        });
 
        b.Entity<AIMessage>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.ConfidenceScore).HasPrecision(5,4);
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            e.HasOne(x => x.Conversation).WithMany(c => c.Messages)
                .HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
 
        b.Entity<AIEscalation>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Status, x.Priority, x.CreatedAt });
            e.HasOne(x => x.Conversation).WithMany(c => c.Escalations)
                .HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
 
        b.Entity<AIKnowledgeEntry>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.CourseId, x.IsActive });
        });
 
        b.Entity<AIAnalyticsEvent>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.EventType, x.OccurredAt });
        });
    }
}