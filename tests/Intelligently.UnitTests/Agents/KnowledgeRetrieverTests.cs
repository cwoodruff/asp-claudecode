using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Entities.AI;
using Intelligently.Infrastructure.Agents.Context;
using Intelligently.Infrastructure.Agents.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Intelligently.UnitTests.Agents;

/// <summary>The retriever scopes by course and module, scores question hits above answer hits, and feeds lookup_knowledge.</summary>
public class KnowledgeRetrieverTests
{
    private static AIKnowledgeEntry Entry(string question, string answer, int? courseId, int? moduleId = null, string? tags = null, bool active = true, int ageDays = 0) => new()
    {
        CourseId = courseId,
        ModuleId = moduleId,
        EntryType = "LabError",
        Question = question,
        Answer = answer,
        Tags = tags,
        IsActive = active,
        AuthoredByUserId = "instructor-1",
        UpdatedAt = DateTime.UtcNow.AddDays(-ageDays),
    };

    private static Task SeedAsync(AgentTestHost host) => host.SaveAsync(db =>
    {
        db.AIKnowledgeEntries.AddRange(
            Entry("What does CS0246 mean?", "The type or namespace could not be found.", courseId: 7, moduleId: 1, tags: "csharp,build"),
            Entry("Why does my build fail with a missing using?", "Usually CS0246: add the using directive.", courseId: 7, moduleId: 2),
            Entry("CS0246 in another course", "Same error, other course.", courseId: 9),
            Entry("General note on namespace errors", "Course-less entry mentioning CS0246.", courseId: null),
            Entry("Inactive CS0246 entry", "Should never be returned.", courseId: 7, active: false),
            Entry("Unrelated question about loops", "Nothing to do with the query.", courseId: 7));
        return Task.CompletedTask;
    });

    private static async Task<IReadOnlyList<KnowledgeMatch>> SearchAsync(AgentTestHost host, string query, int? courseId, int? moduleId = null, int max = 10)
    {
        using var scope = host.Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IKnowledgeRetriever>().SearchAsync(query, courseId, moduleId, max);
    }

    [Fact]
    public async Task Scopes_by_course_keeping_course_less_entries_and_dropping_inactive_ones()
    {
        using var host = new AgentTestHost();
        await SeedAsync(host);

        var matches = await SearchAsync(host, "CS0246 namespace", courseId: 7);

        matches.Select(m => m.Entry.Question).Should().BeEquivalentTo(
            "What does CS0246 mean?",
            "Why does my build fail with a missing using?",
            "General note on namespace errors");
        matches.Should().NotContain(m => m.Entry.CourseId == 9, "another course's entries are out of scope");
        matches.Should().OnlyContain(m => m.Entry.IsActive && m.Score > 0);
    }

    [Fact]
    public async Task Scopes_by_module_within_the_course()
    {
        using var host = new AgentTestHost();
        await SeedAsync(host);

        var matches = await SearchAsync(host, "CS0246", courseId: 7, moduleId: 1);

        matches.Select(m => m.Entry.Question).Should().BeEquivalentTo("What does CS0246 mean?", "General note on namespace errors");
    }

    [Fact]
    public async Task Question_hits_score_above_answer_hits_and_results_are_capped_and_ordered()
    {
        using var host = new AgentTestHost();
        await SeedAsync(host);

        var all = await SearchAsync(host, "CS0246", courseId: 7);
        all[0].Entry.Question.Should().Be("What does CS0246 mean?", "a term in the question outranks the same term in an answer");
        all.Select(m => m.Score).Should().BeInDescendingOrder();
        all[0].Score.Should().Be(3);

        var top = await SearchAsync(host, "CS0246", courseId: 7, max: 1);
        top.Should().ContainSingle().Which.Entry.Question.Should().Be("What does CS0246 mean?");

        (await SearchAsync(host, "CS0246", courseId: null)).Should().HaveCount(4, "no course scope includes every active matching entry");
    }

    [Fact]
    public async Task No_usable_terms_returns_nothing()
    {
        using var host = new AgentTestHost();
        await SeedAsync(host);

        (await SearchAsync(host, "the and of", courseId: 7)).Should().BeEmpty();
        KnowledgeRetriever.Terms("What does CS0246 mean for the build?").Should().Equal("CS0246", "build", "mean");
    }

    [Fact]
    public async Task Lookup_knowledge_tool_returns_scored_entries_through_the_retriever()
    {
        using var host = new AgentTestHost();
        await SeedAsync(host);
        using var scope = host.Provider.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IToolRegistry>();
        registry.TryGetHandler(LookupKnowledgeTool.ToolName, out var handler).Should().BeTrue();
        handler.Descriptor.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("query", "course_id", "module_id");

        var result = await handler.ExecuteAsync(new ToolCall("toolu_1", LookupKnowledgeTool.ToolName,
            JsonSerializer.SerializeToElement(new { query = "CS0246", course_id = 7, module_id = 1 })), StubTool.Context());

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
        var first = doc.RootElement.GetProperty("entries").EnumerateArray().First();
        first.GetProperty("question").GetString().Should().Be("What does CS0246 mean?");
        first.GetProperty("score").GetDouble().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Lookup_knowledge_with_a_null_course_stays_inside_the_run_course()
    {
        using var host = new AgentTestHost();
        await SeedAsync(host);
        using var scope = host.Provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IToolRegistry>().TryGetHandler(LookupKnowledgeTool.ToolName, out var handler).Should().BeTrue();

        var result = await handler.ExecuteAsync(new ToolCall("toolu_1", LookupKnowledgeTool.ToolName,
            JsonSerializer.SerializeToElement(new { query = "CS0246", course_id = (int?)null, module_id = (int?)null })), StubTool.Context(courseId: 7));

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("courseId").GetInt32().Should().Be(7, "a null course means the run's course, never every course");
        var questions = doc.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("question").GetString()).ToList();
        questions.Should().Contain("What does CS0246 mean?").And.Contain("General note on namespace errors");
        questions.Should().NotContain("CS0246 in another course");
    }
}
