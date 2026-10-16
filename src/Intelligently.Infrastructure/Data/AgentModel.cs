using System.Text.Json;
using Intelligently.Core.Agents.Entities;
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
            e.Ignore(x => x.IsActive);
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

            e.HasMany(x => x.Steps).WithOne(s => s.Run).HasForeignKey(s => s.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Plans).WithOne().HasForeignKey(p => p.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Traces).WithOne(t => t.Run).HasForeignKey(t => t.RunId).OnDelete(DeleteBehavior.Cascade);
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
            e.HasIndex(x => x.RunId).IsUnique();
            e.HasIndex(x => new { x.UserId, x.TaskType, x.CreatedAt });
        });
    }
}
