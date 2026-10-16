using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Infrastructure.Agents.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Intelligently.UnitTests.Agents;

/// <summary>Episodes round-trip through the store and recall puts the same learner's episodes first, most similar first.</summary>
public class EpisodicMemoryStoreTests
{
    private static AgentRun Run(string userId, string question, int courseId = 7) => new()
    {
        UserId = userId,
        CourseId = courseId,
        TaskType = "LabErrorDiagnosis",
        TaskSpec = $$"""{"question":"{{question}}"}""",
        Status = AgentRun.StatusCompleted,
    };

    private static async Task<AgentEpisode> WriteAsync(AgentTestHost host, AgentRun run, string summary, DateTime createdAt, IReadOnlyDictionary<string, int>? tools = null)
    {
        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Intelligently.Infrastructure.Data.IntelligentlyDbContext>();
        db.AgentRuns.Add(run);
        var store = scope.ServiceProvider.GetRequiredService<IEpisodicMemoryStore>();
        var episode = await store.WriteAsync(run, run.Status, summary, tools ?? new Dictionary<string, int> { ["lookup_knowledge"] = 2, ["compile"] = 1 });
        episode.CreatedAt = createdAt;
        await db.SaveChangesAsync();
        return episode;
    }

    private static async Task<IReadOnlyList<EpisodeMatch>> RecallAsync(AgentTestHost host, string userId, string taskText, int limit = 10, int courseId = 7)
    {
        using var scope = host.Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEpisodicMemoryStore>().RecallAsync(userId, courseId, "LabErrorDiagnosis", taskText, limit);
    }

    [Fact]
    public async Task Episode_round_trips_with_task_text_tool_usage_and_a_null_embedding()
    {
        using var host = new AgentTestHost();
        var run = Run("learner-1", "CS0246 on build");

        var written = await WriteAsync(host, run, "Explained CS0246 by looking it up.", DateTime.UtcNow);

        var stored = await host.QueryAsync(db => db.AgentEpisodes.AsNoTracking().SingleAsync(e => e.RunId == run.Id));
        stored.Id.Should().Be(written.Id);
        stored.UserId.Should().Be("learner-1");
        stored.CourseId.Should().Be(7);
        stored.TaskType.Should().Be("LabErrorDiagnosis");
        stored.TaskText.Should().Be("LabErrorDiagnosis CS0246 on build");
        stored.Outcome.Should().Be(AgentRun.StatusCompleted);
        stored.Summary.Should().Be("Explained CS0246 by looking it up.");
        stored.ToolUsageJson.Should().Be("""{"compile":1,"lookup_knowledge":2}""", "tool usage is stored with sorted keys");
        stored.Embedding.Should().BeNull("Phase 3 stores the column but does not call an embedding API");

        var recalled = (await RecallAsync(host, "learner-1", "LabErrorDiagnosis CS0246 on build")).Should().ContainSingle().Subject;
        recalled.SameLearner.Should().BeTrue();
        recalled.Similarity.Should().Be(1);
        recalled.Episode.Summary.Should().Be(stored.Summary);
    }

    [Fact]
    public async Task Embedding_round_trips_as_json_when_present()
    {
        using var host = new AgentTestHost();
        var run = Run("learner-1", "vector");
        await host.SaveAsync(db =>
        {
            db.AgentRuns.Add(run);
            db.AgentEpisodes.Add(new AgentEpisode { RunId = run.Id, UserId = run.UserId, CourseId = 7, TaskType = run.TaskType, TaskText = "vector", Outcome = "Completed", Summary = "s", Embedding = [0.25f, -1f, 3.5f] });
            return Task.CompletedTask;
        });

        var stored = await host.QueryAsync(db => db.AgentEpisodes.AsNoTracking().SingleAsync(e => e.RunId == run.Id));
        stored.Embedding.Should().Equal(0.25f, -1f, 3.5f);
    }

    [Fact]
    public async Task Recall_returns_the_same_learner_first_then_most_similar_then_most_recent()
    {
        using var host = new AgentTestHost();
        var now = DateTime.UtcNow;
        // Another learner with an almost identical task: similar, but must come after this learner's episodes.
        await WriteAsync(host, Run("learner-2", "CS0246 on build in lab 3"), "other learner, near-identical task", now);
        // This learner: one close match (older) and one distant match (newer).
        await WriteAsync(host, Run("learner-1", "CS0246 on build in lab 3"), "this learner, close match", now.AddDays(-5));
        await WriteAsync(host, Run("learner-1", "NullReferenceException in unit test"), "this learner, distant match", now.AddDays(-1));
        // A different task type is never recalled.
        await host.SaveAsync(db =>
        {
            var run = Run("learner-1", "CS0246 on build in lab 3");
            run.TaskType = "Grading";
            db.AgentRuns.Add(run);
            db.AgentEpisodes.Add(new AgentEpisode { RunId = run.Id, UserId = run.UserId, CourseId = 7, TaskType = "Grading", TaskText = "Grading CS0246 on build in lab 3", Outcome = "Completed", Summary = "grading" });
            return Task.CompletedTask;
        });

        var matches = await RecallAsync(host, "learner-1", "LabErrorDiagnosis CS0246 on build in lab 3");

        matches.Select(m => m.Episode.Summary).Should().Equal(
            "this learner, close match",
            "this learner, distant match",
            "other learner, near-identical task");
        matches[0].SameLearner.Should().BeTrue();
        matches[0].Similarity.Should().BeGreaterThan(matches[1].Similarity);
        matches[2].SameLearner.Should().BeFalse();
        matches[2].Similarity.Should().BeGreaterThan(0.9);

        (await RecallAsync(host, "learner-1", "LabErrorDiagnosis CS0246 on build in lab 3", limit: 1))
            .Should().ContainSingle().Which.Episode.Summary.Should().Be("this learner, close match");
    }

    [Fact]
    public async Task Episodes_of_other_courses_are_never_recalled_even_for_the_same_learner()
    {
        using var host = new AgentTestHost();
        var now = DateTime.UtcNow;
        await WriteAsync(host, Run("learner-1", "CS0246 on build", courseId: 8), "same learner, other course", now);
        await WriteAsync(host, Run("learner-2", "CS0246 on build", courseId: 8), "other learner, other course", now);
        await WriteAsync(host, Run("learner-2", "CS0246 on build", courseId: 7), "other learner, this course", now.AddDays(-1));

        var matches = await RecallAsync(host, "learner-1", "LabErrorDiagnosis CS0246 on build", courseId: 7);

        matches.Select(m => m.Episode.Summary).Should().Equal("other learner, this course");
        (await RecallAsync(host, "learner-1", "LabErrorDiagnosis CS0246 on build", courseId: 8))
            .Select(m => m.Episode.Summary).Should().Equal("same learner, other course", "other learner, other course");
    }

    [Fact]
    public void Trigram_similarity_is_one_for_identical_text_and_zero_for_disjoint_text()
    {
        TrigramSimilarity.Compute("CS0246 on build", "cs0246  ON build!").Should().Be(1);
        TrigramSimilarity.Compute("abc", "xyz").Should().Be(0);
        TrigramSimilarity.Compute("CS0246 on build", "CS0246 on build in lab 3").Should().BeInRange(0.4, 0.99);
    }
}
