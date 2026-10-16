using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Entities.AI;
using Intelligently.Infrastructure.Agents.Context;
using Intelligently.Infrastructure.Agents.Execution;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// The assembler builds the envelope stable-first: the prefix (system + tools) is byte-identical across
/// turns, retrieved knowledge and episodes precede the transcript, and the volatile stage instruction is a
/// system message inside messages[] rather than the system field.
/// </summary>
public class ContextAssemblerTests
{
    private static readonly string CompactionBlockJson =
        """[{"type":"compaction","content":"Earlier turns summarized.","encrypted_content":"enc-123","signature":"sig-1"},{"type":"text","text":"Looking that up."},{"type":"tool_use","id":"toolu_01","name":"lookup_knowledge","input":{"query":"CS0246","course_id":null,"module_id":null}}]""";

    private static async Task SeedAsync(AgentTestHost host)
    {
        await host.SaveAsync(db =>
        {
            db.AIKnowledgeEntries.Add(new AIKnowledgeEntry
            {
                CourseId = 7, EntryType = "LabError", Question = "What does CS0246 mean?",
                Answer = "CS0246: the type or namespace could not be found.", Tags = "csharp,build", AuthoredByUserId = "instructor-1",
            });
            db.AgentEpisodes.Add(new AgentEpisode
            {
                RunId = Guid.NewGuid(), UserId = "learner-1", CourseId = 7, TaskType = "LabErrorDiagnosis",
                TaskText = "LabErrorDiagnosis CS0246 on build", Outcome = AgentRun.StatusCompleted,
                Summary = "Explained CS0246 by looking it up.", ToolUsageJson = """{"lookup_knowledge":1}""",
                CreatedAt = DateTime.UtcNow.AddDays(-1),
            });
            return Task.CompletedTask;
        });
    }

    /// <summary>A two-step plan so there are two consecutive Act turns; the decision replays stored content.</summary>
    private static List<PromptEnvelope> CaptureEnvelopes(AgentTestHost host)
    {
        var captured = new List<PromptEnvelope>();
        host.Engine.Setup(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<PromptEnvelope, CancellationToken>((e, _) => captured.Add(e))
            .ReturnsAsync(new PlanResult(StubTool.PlanFor(LookupKnowledgeTool.ToolName, steps: 2), EngineStubs.Usage, "{}"));
        host.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<PromptEnvelope, CancellationToken>((e, _) => captured.Add(e))
            .ReturnsAsync(new DecisionResult([EngineStubs.LookupCall()], "Looking that up.", "tool_use", EngineStubs.Usage, "{}", CompactionBlockJson));
        return captured;
    }

    [Fact]
    public async Task Prefix_is_byte_identical_across_consecutive_turns_of_one_run()
    {
        using var host = new AgentTestHost();
        await SeedAsync(host);
        var envelopes = CaptureEnvelopes(host);

        var run = await host.CreateRunAsync();
        var (final, _) = await host.RunToEndAsync(run.Id);

        final.Status.Should().Be(AgentRun.StatusCompleted);
        envelopes.Should().HaveCount(3, "one Plan turn and two Act turns");
        var prefixes = envelopes.Select(e => e.SerializePrefix()).Distinct().ToList();
        prefixes.Should().ContainSingle("system blocks and tools must serialize identically on every turn");
        envelopes.Select(e => e.PrefixHash()).Distinct().Should().ContainSingle();

        envelopes.Should().OnlyContain(e => e.System.Count == 1 && e.System[0].CacheBreakpoint && e.System[0].Text == ContextAssembler.SystemPrompt);
        envelopes.Should().OnlyContain(e => e.CacheTools && e.Tools.Count == 1 && e.Tools[0].Name == LookupKnowledgeTool.ToolName);
        envelopes.Should().OnlyContain(e => e.RunId == run.Id && e.InputTokens > 0);
    }

    [Fact]
    public async Task Stage_instruction_is_a_system_message_in_messages_and_never_in_the_system_field()
    {
        using var host = new AgentTestHost();
        await SeedAsync(host);
        var envelopes = CaptureEnvelopes(host);

        var run = await host.CreateRunAsync();
        await host.RunToEndAsync(run.Id);

        foreach (var envelope in envelopes)
        {
            var last = envelope.Messages[^1];
            last.IsSystem.Should().BeTrue();
            last.Section.Should().Be(PromptSection.Instruction);
            envelope.Messages.Take(envelope.Messages.Count - 1).Should().OnlyContain(m => !m.IsSystem, "only the trailing instruction is a system message");
            envelope.SystemPrompt.Should().NotContain(last.Text);
        }

        envelopes[0].Stage.Should().Be(AgentStage.Plan);
        envelopes[0].Messages[^1].Text.Should().Contain("Produce the plan").And.Contain($"- {LookupKnowledgeTool.ToolName}:");
        envelopes[1].Stage.Should().Be(AgentStage.Act);
        envelopes[1].Messages[^1].Text.Should().Contain("step 0").And.Contain($"Call the tool '{LookupKnowledgeTool.ToolName}'");
        envelopes[2].Messages[^1].Text.Should().Contain("step 1");
    }

    [Fact]
    public async Task Sections_follow_the_stable_to_volatile_order_and_the_transcript_replays_stored_response_content()
    {
        using var host = new AgentTestHost();
        await SeedAsync(host);
        var envelopes = CaptureEnvelopes(host);

        var run = await host.CreateRunAsync();
        await host.RunToEndAsync(run.Id);

        var sections = envelopes[2].Messages.Select(m => m.Section).ToList();
        sections.Should().BeInAscendingOrder();
        sections.Should().Contain(PromptSection.Knowledge).And.Contain(PromptSection.Episodic).And.Contain(PromptSection.Transcript).And.EndWith(PromptSection.Instruction);

        var knowledge = envelopes[2].Messages.Single(m => m.Section == PromptSection.Knowledge);
        knowledge.Role.Should().Be(PromptMessage.UserRole);
        knowledge.Content.Should().HaveCount(2, "a header block and one block per retrieved entry");
        knowledge.Content[1].Text.Should().Contain("CS0246");

        var episodic = envelopes[2].Messages.Single(m => m.Section == PromptSection.Episodic);
        episodic.Content[1].Text.Should().Contain("Explained CS0246").And.Contain("this learner");

        var transcript = envelopes[2].Messages.Where(m => m.Section == PromptSection.Transcript).ToList();
        transcript[0].Role.Should().Be(PromptMessage.UserRole);
        transcript[0].Text.Should().Contain("Task type: LabErrorDiagnosis");
        transcript.Select(m => m.Role).Should().Equal("user", "assistant", "user", "assistant", "user");

        // The stored response content (compaction block included) is replayed verbatim as the assistant turn.
        var replayed = transcript[3];
        replayed.Content.Should().OnlyContain(c => c.Kind == PromptContent.RawKind);
        replayed.Content.Select(c => c.Raw!.Value.GetProperty("type").GetString()).Should().Equal("compaction", "text", "tool_use");
        replayed.Content[0].Raw!.Value.GetProperty("encrypted_content").GetString().Should().Be("enc-123");

        // Every tool_use is answered by a tool_result in the next user message, which ends with the verdict and the cache breakpoint.
        var results = transcript[4];
        results.Content[0].Kind.Should().Be(PromptContent.ToolResultKind);
        results.Content[0].ToolUseId.Should().Be("toolu_01");
        results.Content[^1].Text.Should().StartWith("Verifier");
        results.Content[^1].CacheBreakpoint.Should().BeTrue("the last transcript block carries the incremental cache breakpoint");
        envelopes[2].Messages[^1].Content.Should().OnlyContain(c => !c.CacheBreakpoint, "the volatile instruction is never cached");

        var step = await host.QueryAsync(db => db.AgentSteps.Where(s => s.RunId == run.Id).OrderBy(s => s.PlanStepIndex).FirstAsync());
        step.ResponseContentJson.Should().Be(CompactionBlockJson);
        step.PromptTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Without_stored_response_content_the_transcript_is_synthesized_from_text_and_tool_calls()
    {
        using var host = new AgentTestHost();
        EngineStubs.PlanReturns(host.Engine, StubTool.PlanFor(LookupKnowledgeTool.ToolName, steps: 2));
        var captured = new List<PromptEnvelope>();
        host.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<PromptEnvelope, CancellationToken>((e, _) => captured.Add(e))
            .ReturnsAsync(EngineStubs.Decision(EngineStubs.LookupCall()));

        var run = await host.CreateRunAsync();
        await host.RunToEndAsync(run.Id);

        captured.Should().HaveCount(2);
        var assistant = captured[1].Messages.Where(m => m.Section == PromptSection.Transcript).Last(m => m.Role == PromptMessage.AssistantRole);
        assistant.Content.Select(c => c.Kind).Should().Equal(PromptContent.TextKind, PromptContent.ToolUseKind);
        assistant.Content[1].ToolName.Should().Be(LookupKnowledgeTool.ToolName);
        assistant.Content[1].Input!.Value.GetProperty("query").GetString().Should().Be("CS0246");
        captured[1].Messages.Should().NotContain(m => m.Section == PromptSection.Knowledge, "nothing was seeded");
    }
}
