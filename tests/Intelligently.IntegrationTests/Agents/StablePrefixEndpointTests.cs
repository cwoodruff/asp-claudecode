using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Entities.AI;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Intelligently.IntegrationTests.Agents;

/// <summary>
/// In the Web host, with the real Context Plane and a mocked engine, every model turn of a run gets the
/// same serialized prefix (system blocks and tools) and the stage instruction as a trailing system message.
/// </summary>
public class StablePrefixEndpointTests : IClassFixture<AgentWebFactory>
{
    private static readonly ModelUsage Usage = new("claude-opus-5-5", 100, 20, 80, 0);
    private readonly AgentWebFactory _factory;

    public StablePrefixEndpointTests(AgentWebFactory factory)
    {
        _factory = factory;
    }

    private static async Task<string> PollUntilSettledAsync(HttpClient client, Guid id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string html;
        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/agent/runs/{id}");
            request.Headers.Add("HX-Request", "true");
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            html = await response.Content.ReadAsStringAsync();
            if (!Regex.IsMatch(html, "data-status=\"Running\"")) return html;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);

        return html;
    }

    [Fact]
    public async Task Every_turn_of_a_run_shares_one_prefix_and_ends_with_the_stage_instruction()
    {
        var envelopes = new List<PromptEnvelope>();
        _factory.Engine.Reset();
        _factory.Engine.Setup(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<PromptEnvelope, CancellationToken>((e, _) => { lock (envelopes) envelopes.Add(e); })
            .ReturnsAsync(new PlanResult(new AgentPlan
            {
                Goal = "Explain CS0246.",
                Steps =
                [
                    new PlanStep { Index = 0, Description = "Look up CS0246.", Tool = "lookup_knowledge", SuccessCriteria = "An entry is found." },
                    new PlanStep { Index = 1, Description = "Look up the fix.", Tool = "lookup_knowledge", SuccessCriteria = "An entry is found." },
                ],
            }, Usage, "{}"));
        _factory.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<PromptEnvelope, CancellationToken>((e, _) => { lock (envelopes) envelopes.Add(e); })
            .ReturnsAsync(new DecisionResult(
                [new ToolCall("toolu_01", "lookup_knowledge", JsonSerializer.SerializeToElement(new { query = "CS0246", course_id = (int?)null, module_id = (int?)null }))],
                "Looking it up.", "tool_use", Usage, "{}",
                """[{"type":"text","text":"Looking it up."},{"type":"tool_use","id":"toolu_01","name":"lookup_knowledge","input":{"query":"CS0246","course_id":null,"module_id":null}}]"""));
        await _factory.WithDbAsync(db =>
        {
            db.AIKnowledgeEntries.Add(new AIKnowledgeEntry
            {
                CourseId = 11, EntryType = "LabError", Question = "What does CS0246 mean?",
                Answer = "A type or namespace was not found.", AuthoredByUserId = "instructor-1",
            });
            return Task.CompletedTask;
        });
        var client = _factory.CreateClient();

        var created = await client.PostAsJsonAsync("/agent/runs", new
        {
            userId = "learner-prefix", courseId = 11, taskType = "LabErrorDiagnosis", taskSpec = new { question = "My build fails with CS0246." },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var html = await PollUntilSettledAsync(client, id, TimeSpan.FromSeconds(30));
        html.Should().Contain("data-status=\"Completed\"");

        List<PromptEnvelope> turns;
        lock (envelopes) turns = envelopes.Where(e => e.RunId == id).ToList();
        turns.Should().HaveCount(3, "one Plan turn and two Act turns");
        turns.Select(e => e.SerializePrefix()).Distinct().Should().ContainSingle("the prefix is stable across turns");
        turns.Should().OnlyContain(e => e.Messages[e.Messages.Count - 1].IsSystem && e.Messages[e.Messages.Count - 1].Section == PromptSection.Instruction);
        turns.Should().OnlyContain(e => e.InputTokens > 0);
        turns[2].Messages.Should().Contain(m => m.Section == PromptSection.Knowledge);
        turns[2].Messages.Count(m => m.Role == PromptMessage.AssistantRole).Should().Be(2, "the plan and the first step are replayed in the transcript");

        await _factory.WithDbAsync(async db =>
        {
            var steps = await db.AgentSteps.Where(s => s.RunId == id).OrderBy(s => s.PlanStepIndex).ToListAsync();
            steps.Should().HaveCount(2);
            steps.Should().OnlyContain(s => s.PromptTokens > 0 && s.ResponseContentJson != null);
            var trace = await db.AgentTraces.Where(t => t.RunId == id && t.Kind == AgentTrace.KindModelCall).FirstAsync();
            trace.CacheReadInputTokens.Should().Be(80);
            trace.Payload.Should().Contain("\"prefixHash\"");
            (await db.AgentEpisodes.SingleAsync(e => e.RunId == id)).TaskText.Should().Contain("CS0246");
        });
    }
}
