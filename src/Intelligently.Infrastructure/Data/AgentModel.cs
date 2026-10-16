using System.Text.Json;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Entities.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Intelligently.Infrastructure.Data;

/// <summary>EF Core configuration for the agent tables (plan section 2.1).</summary>
internal static class AgentModel
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<AgentRun>(e =>
        {
            e.ToTable("AgentRuns");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.TaskType).HasMaxLength(100);
            e.Property(x => x.Stage).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.RecoveryReason).HasMaxLength(30);
            e.Property(x => x.TotalCostUsd).HasPrecision(12, 6);
            e.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            // The lease expiry is a second concurrency token: a worker whose lease was taken over cannot commit its stale stage.
            e.Property(x => x.ClaimedBy).HasMaxLength(200);
            e.Property(x => x.ClaimedUntil).IsConcurrencyToken();
            e.Ignore(x => x.IsActive);
            e.Ignore(x => x.IsWaitingForApproval);
            e.HasIndex(x => new { x.Status, x.Stage });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });

            e.OwnsOne(x => x.Budget, budget =>
            {
                budget.Property(p => p.ReplansLeft).HasColumnName("Budget_ReplansLeft");
                budget.Property(p => p.RetriesLeft).HasColumnName("Budget_RetriesLeft");
                budget.Property(p => p.ToolCallsLeft).HasColumnName("Budget_ToolCallsLeft");
                budget.Property(p => p.TokensLeft).HasColumnName("Budget_TokensLeft");
                budget.Property(p => p.WallClockLeft)
                    .HasColumnName("Budget_WallClockTicksLeft")
                    .HasConversion(new TimeSpanToTicksConverter());
                budget.Ignore(p => p.IsExhausted);
            });
            e.Navigation(x => x.Budget).IsRequired();

            e.OwnsOne(x => x.Principal, principal =>
            {
                principal.Property(p => p.UserId).HasColumnName("Principal_UserId").HasMaxLength(450);
                principal.Property(p => p.Roles)
                    .HasColumnName("Principal_RolesJson")
                    .HasConversion(JsonListConverter<string>(), JsonListComparer<string>());
                principal.Property(p => p.CourseScope)
                    .HasColumnName("Principal_CourseScopeJson")
                    .HasConversion(JsonListConverter<int>(), JsonListComparer<int>());
                principal.Ignore(p => p.IsAuthenticated);
            });
            e.Navigation(x => x.Principal).IsRequired();

            e.HasMany(x => x.Steps).WithOne(s => s.Run).HasForeignKey(s => s.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Plans).WithOne().HasForeignKey(p => p.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Traces).WithOne(t => t.Run).HasForeignKey(t => t.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Approvals).WithOne(a => a.Run).HasForeignKey(a => a.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AgentStep>(e =>
        {
            e.ToTable("AgentSteps");
            e.HasKey(x => x.Id);
            e.Property(x => x.Stage).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Status).HasMaxLength(20);
            e.HasIndex(x => new { x.RunId, x.PlanRevision, x.PlanStepIndex, x.Attempt }).IsUnique();
        });

        b.Entity<AgentPlan>(e =>
        {
            e.ToTable("AgentPlans");
            e.HasKey(x => x.Id);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.Steps)
                .HasConversion(
                    steps => JsonSerializer.Serialize(steps, (JsonSerializerOptions?)null),
                    json => JsonSerializer.Deserialize<List<PlanStep>>(json, (JsonSerializerOptions?)null) ?? new List<PlanStep>(),
                    new ValueComparer<IReadOnlyList<PlanStep>>(
                        (a, c) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(c, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
                        v => JsonSerializer.Deserialize<List<PlanStep>>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)!))
                .HasColumnName("StepsJson");
            e.HasIndex(x => new { x.RunId, x.Revision });
        });

        b.Entity<AgentTrace>(e =>
        {
            e.ToTable("AgentTraces");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Stage).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Kind).HasMaxLength(30);
            e.Property(x => x.Model).HasMaxLength(100);
            e.HasIndex(x => new { x.RunId, x.CreatedAt });
        });

        b.Entity<AgentEpisode>(e =>
        {
            e.ToTable("AgentEpisodes");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.TaskType).HasMaxLength(100);
            e.Property(x => x.Outcome).HasMaxLength(20);
            e.Property(x => x.Embedding)
                .HasColumnName("EmbeddingJson")
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<float[]>(v, (JsonSerializerOptions?)null),
                    new ValueComparer<float[]?>(
                        (a, c) => (a == null && c == null) || (a != null && c != null && a.SequenceEqual(c)),
                        v => v == null ? 0 : v.Length,
                        v => v == null ? null : v.ToArray()));
            e.HasIndex(x => x.RunId).IsUnique();
            e.HasIndex(x => new { x.UserId, x.TaskType, x.CreatedAt });
            e.HasIndex(x => new { x.TaskType, x.CreatedAt });
        });

        b.Entity<AgentApproval>(e =>
        {
            e.ToTable("AgentApprovals");
            e.HasKey(x => x.Id);
            e.Property(x => x.ToolName).HasMaxLength(200);
            e.Property(x => x.ArgsHash).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.DecidedBy).HasMaxLength(450);
            e.Ignore(x => x.IsPending);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.RunId, x.CreatedAt });
            e.HasIndex(x => new { x.RunId, x.ToolName, x.ArgsHash });
            e.HasOne(x => x.Escalation).WithMany()
                .HasForeignKey(x => x.EscalationId).IsRequired(false).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("AgentIdempotencyRecords");
            e.HasKey(x => x.Id);
            e.Property(x => x.ToolName).HasMaxLength(200);
            e.Property(x => x.ArgsHash).HasMaxLength(64);
            e.Property(x => x.ToolCallId).HasMaxLength(100);
            e.HasIndex(x => new { x.RunId, x.ToolName, x.ArgsHash }).IsUnique();
            e.HasOne(x => x.Run).WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static ValueConverter<IReadOnlyList<T>, string> JsonListConverter<T>() => new(
        list => JsonSerializer.Serialize(list, (JsonSerializerOptions?)null),
        json => JsonSerializer.Deserialize<List<T>>(json, (JsonSerializerOptions?)null) ?? new List<T>());

    private static ValueComparer<IReadOnlyList<T>> JsonListComparer<T>() => new(
        (a, c) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(c, (JsonSerializerOptions?)null),
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
        v => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)!);
}
