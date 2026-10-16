using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Entities.AI;
using Intelligently.Infrastructure.Agents.Learn;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// Learn (plan section 2.2, Learn row; Phase 5): exactly one episode per run, written by the Learn stage on
/// completion; an instructor marking an escalation reusable publishes a knowledge entry linked from the episode.
/// </summary>
public class LearnStageTests
{
    private static Task SeedKnowledgeAsync(AgentTestHost host) => host.SaveAsync(db =>
    {
        db.AIKnowledgeEntries.Add(new AIKnowledgeEntry
        {
            CourseId = 7, EntryType = "LabError", Question = "What does CS0246 mean?",
            Answer = "CS0246: the type or namespace could not be found.", Tags = "csharp,build", AuthoredByUserId = "instructor-1",
        });
        return Task.CompletedTask;
    });

    private static async Task<AgentRun> CompleteRunAsync(AgentTestHost host)
    {
        await SeedKnowledgeAsync(host);
        EngineStubs.PlanReturns(host.Engine, EngineStubs.LookupPlan());
        EngineStubs.DecideReturns(host.Engine, EngineStubs.LookupCall());
        var run = await host.CreateRunAsync();
        var (final, _) = await host.RunToEndAsync(run.Id);
        final.Status.Should().Be(AgentRun.StatusCompleted);
        return final;
    }

    private static async Task<AgentRun> EscalateRunAsync(AgentTestHost host)
    {
        EngineStubs.PlanFails(host.Engine, "no plan");
        var run = await host.CreateRunAsync();
        var (final, _) = await host.RunToEndAsync(run.Id);
        final.Status.Should().Be(AgentRun.StatusFailed);
        return final;
    }

    private static async Task<EscalationResolution> ResolveAsync(AgentTestHost host, int escalationId, string response, bool addToKnowledgeBase, string instructor = "instructor-9")
    {
        using var scope = host.Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEscalationResolver>().ResolveAsync(escalationId, instructor, response, addToKnowledgeBase);
    }

    [Fact]
    public async Task Learn_writes_exactly_one_episode_per_completed_run()
    {
        using var host = new AgentTestHost();
        var run = await CompleteRunAsync(host);

        var episodes = await host.QueryAsync(db => db.AgentEpisodes.Where(e => e.RunId == run.Id).ToListAsync());
        var episode = episodes.Should().ContainSingle().Subject;
        episode.Outcome.Should().Be(AgentRun.StatusCompleted);
        episode.UserId.Should().Be("learner-1");
        episode.TaskText.Should().Be("LabErrorDiagnosis CS0246 on build");
        episode.ToolUsageJson.Should().Be("""{"lookup_knowledge":1}""");
        episode.KnowledgeEntryId.Should().BeNull("nothing has been marked reusable yet");
        run.StatusReason.Should().Contain($"Episode {episode.Id}");

        // The writer is idempotent: asking again for the same run returns the row it already has.
        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntelligentlyDbContext>();
        var tracked = await db.AgentRuns.SingleAsync(r => r.Id == run.Id);
        var again = await scope.ServiceProvider.GetRequiredService<EpisodeWriter>().WriteAsync(tracked, AgentRun.StatusCompleted);
        await db.SaveChangesAsync();
        again.Id.Should().Be(episode.Id);
        (await host.QueryAsync(d => d.AgentEpisodes.CountAsync(e => e.RunId == run.Id))).Should().Be(1);
    }

    [Fact]
    public async Task Marking_an_escalation_reusable_publishes_a_knowledge_entry_and_links_the_episode()
    {
        using var host = new AgentTestHost();
        var run = await CompleteRunAsync(host);
        // An instructor-facing escalation raised during the run (as create_escalation would).
        var escalation = new AIEscalation
        {
            AgentRunId = run.Id, UserId = run.UserId, CourseId = 7, ModuleId = 3,
            LearnerQuestion = "Why does my build say CS0246 for Newtonsoft?",
            EscalationReason = "Agent needed an instructor.", Priority = "Medium", Status = "New",
        };
        await host.SaveAsync(db => { db.AIEscalations.Add(escalation); return Task.CompletedTask; });

        var resolution = await ResolveAsync(host, escalation.Id, "Add the Newtonsoft.Json package reference to the project.", addToKnowledgeBase: true);

        resolution.KnowledgeEntry.Should().NotBeNull();
        resolution.Episode.Should().NotBeNull();
        var entry = await host.QueryAsync(db => db.AIKnowledgeEntries.SingleAsync(k => k.SourceEscalationId == escalation.Id));
        entry.Question.Should().Be("Why does my build say CS0246 for Newtonsoft?");
        entry.Answer.Should().Be("Add the Newtonsoft.Json package reference to the project.");
        entry.CourseId.Should().Be(7);
        entry.ModuleId.Should().Be(3);
        entry.EntryType.Should().Be("LabError", "the task type is LabErrorDiagnosis");
        entry.Tags.Should().Contain("LabErrorDiagnosis");
        entry.AuthoredByUserId.Should().Be("instructor-9");
        entry.IsActive.Should().BeTrue();

        var episode = await host.QueryAsync(db => db.AgentEpisodes.SingleAsync(e => e.RunId == run.Id));
        episode.KnowledgeEntryId.Should().Be(entry.Id, "the episode is linked to the entry it produced");
        episode.Id.Should().Be(resolution.Episode!.Id, "the completed run keeps the episode Learn wrote; no second one");

        var resolved = await host.QueryAsync(db => db.AIEscalations.SingleAsync(e => e.Id == escalation.Id));
        resolved.Status.Should().Be("Resolved");
        resolved.AddToKnowledgeBase.Should().BeTrue();
        resolved.InstructorUserId.Should().Be("instructor-9");
        resolved.ResolvedAt.Should().NotBeNull();

        (await host.QueryAsync(db => db.AgentTraces.CountAsync(t => t.RunId == run.Id && t.Stage == AgentStage.Learn && t.Kind == AgentTrace.KindNote && t.Payload.Contains("knowledgeEntryId"))))
            .Should().Be(1, "the link is evidence too");

        // Resolving again with the flag does not publish a second entry.
        await ResolveAsync(host, escalation.Id, "Same answer, reworded.", addToKnowledgeBase: true);
        (await host.QueryAsync(db => db.AIKnowledgeEntries.CountAsync(k => k.SourceEscalationId == escalation.Id))).Should().Be(1);
    }

    [Fact]
    public async Task An_escalated_run_gets_its_episode_when_the_instructor_publishes_the_outcome()
    {
        using var host = new AgentTestHost();
        var run = await EscalateRunAsync(host);
        (await host.QueryAsync(db => db.AgentEpisodes.CountAsync(e => e.RunId == run.Id))).Should().Be(0, "Recover escalates straight to Failed, Learn never runs");
        var escalation = await host.QueryAsync(db => db.AIEscalations.SingleAsync(e => e.AgentRunId == run.Id));

        var resolution = await ResolveAsync(host, escalation.Id, "The planner cannot handle this spec; here is the fix.", addToKnowledgeBase: true);

        var episode = await host.QueryAsync(db => db.AgentEpisodes.SingleAsync(e => e.RunId == run.Id));
        episode.Outcome.Should().Be(AgentRun.StatusFailed);
        episode.KnowledgeEntryId.Should().Be(resolution.KnowledgeEntry!.Id);
        episode.Summary.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Resolving_without_the_flag_records_the_response_but_publishes_nothing()
    {
        using var host = new AgentTestHost();
        var run = await EscalateRunAsync(host);
        var escalation = await host.QueryAsync(db => db.AIEscalations.SingleAsync(e => e.AgentRunId == run.Id));

        var resolution = await ResolveAsync(host, escalation.Id, "Handled offline.", addToKnowledgeBase: false);

        resolution.KnowledgeEntry.Should().BeNull();
        resolution.Episode.Should().BeNull();
        (await host.QueryAsync(db => db.AIKnowledgeEntries.CountAsync())).Should().Be(0);
        (await host.QueryAsync(db => db.AgentEpisodes.CountAsync(e => e.RunId == run.Id))).Should().Be(0);
        var resolved = await host.QueryAsync(db => db.AIEscalations.SingleAsync(e => e.Id == escalation.Id));
        resolved.Status.Should().Be("Resolved");
        resolved.InstructorResponse.Should().Be("Handled offline.");
        resolved.AddToKnowledgeBase.Should().BeFalse();
    }

    [Fact]
    public async Task Resolving_needs_an_instructor_a_response_and_an_existing_escalation()
    {
        using var host = new AgentTestHost();

        var noInstructor = () => ResolveAsync(host, 1, "x", false, instructor: " ");
        await noInstructor.Should().ThrowAsync<ArgumentException>();
        var noResponse = () => ResolveAsync(host, 1, "  ", false);
        await noResponse.Should().ThrowAsync<ArgumentException>();
        var missing = () => ResolveAsync(host, 12345, "x", true);
        await missing.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Theory]
    [InlineData("LabErrorDiagnosis", "LabError")]
    [InlineData("MisconceptionCheck", "Misconception")]
    [InlineData("BestPracticeReview", "BestPractice")]
    [InlineData("QuizGeneration", "Clarification")]
    [InlineData(null, "FAQ")]
    public void Entry_type_follows_the_task_type(string? taskType, string expected) =>
        EscalationResolver.EntryTypeFor(taskType).Should().Be(expected);
}
