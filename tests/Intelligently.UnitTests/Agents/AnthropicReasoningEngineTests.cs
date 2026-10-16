using System.Text.Json;
using Anthropic;
using FluentAssertions;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Exceptions;
using Intelligently.Infrastructure.Agents.Intelligence;
using Intelligently.UnitTests.AI;
using Microsoft.Extensions.Options;

namespace Intelligently.UnitTests.Agents;

/// <summary>Wire-format checks for the Intelligence Plane against a fake HTTP handler.</summary>
public class AnthropicReasoningEngineTests
{
    internal static (AnthropicReasoningEngine Engine, FakeAnthropicHandler Handler) Build(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeAnthropicHandler(responder);
        var client = new AnthropicClient { ApiKey = "test-key", HttpClient = new HttpClient(handler), MaxRetries = 0 };
        var engine = new AnthropicReasoningEngine(client, Options.Create(new AgentsOptions()));
        return (engine, handler);
    }

    private static (AnthropicReasoningEngine Engine, FakeAnthropicHandler Handler) Build(string responseJson) =>
        Build(_ => FakeAnthropicHandler.Json(responseJson));

    internal static readonly ToolDescriptor LookupTool = new(
        "lookup_knowledge",
        "Search the knowledge base.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { query = new { type = "string" } },
            required = new[] { "query" },
            additionalProperties = false,
        }),
        ToolRiskLevel.ReadOnly);

    internal static PromptEnvelope Envelope() => new(
        "You are a tutor.",
        [LookupTool],
        [
            new PromptMessage("user", "Plan it."),
            new PromptMessage("system", "Produce the plan now.", PromptSection.Instruction),
        ]);

    internal static string Response(string contentJson, string stopReason = "end_turn") => $$"""
        {
          "id": "msg_01", "type": "message", "role": "assistant", "model": "claude-opus-5-5",
          "content": {{contentJson}},
          "stop_reason": "{{stopReason}}", "stop_sequence": null,
          "usage": { "input_tokens": 300, "output_tokens": 50, "cache_read_input_tokens": 200, "cache_creation_input_tokens": 0 }
        }
        """;

    [Fact]
    public async Task PlanAsync_requests_structured_output_from_the_AgentPlan_schema_and_parses_the_plan()
    {
        var planJson = """{"goal":"Explain CS0246","steps":[{"index":0,"description":"Look it up","tool":"lookup_knowledge","success_criteria":"entry found"}]}""";
        var (engine, handler) = Build(Response($$"""[{"type":"text","text":{{JsonSerializer.Serialize(planJson)}}}]"""));

        var result = await engine.PlanAsync(Envelope());

        result.Succeeded.Should().BeTrue();
        result.Plan!.Goal.Should().Be("Explain CS0246");
        result.Plan.Steps.Should().ContainSingle().Which.Tool.Should().Be("lookup_knowledge");
        result.Usage.Should().Be(new ModelUsage("claude-opus-5-5", 300, 50, 200, 0));
        result.ContentJson.Should().Contain("\"type\":\"text\"");

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        root.GetProperty("model").GetString().Should().Be("claude-opus-5-5");
        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("adaptive");
        var outputConfig = root.GetProperty("output_config");
        outputConfig.GetProperty("effort").GetString().Should().Be("high");
        var format = outputConfig.GetProperty("format");
        format.GetProperty("type").GetString().Should().Be("json_schema");
        var schema = format.GetProperty("schema");
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("goal", "steps");
        // The tool list is sent on every stage so the cached prefix is identical; planning just may not call them.
        root.GetProperty("tools").EnumerateArray().Should().HaveCount(1);
        root.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("none");
        handler.LastRequest!.Headers.Contains("anthropic-beta").Should().BeFalse("no compaction was requested");
    }

    [Fact]
    public async Task PlanAsync_reports_a_failure_when_the_output_does_not_match_the_schema()
    {
        var (engine, _) = Build(Response("""[{"type":"text","text":"{\"goal\":\"x\"}"}]"""));

        var result = await engine.PlanAsync(Envelope());

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("schema");
    }

    [Fact]
    public async Task DecideAsync_sends_strict_tools_with_tool_choice_auto_and_parses_tool_use_blocks()
    {
        var (engine, handler) = Build(Response("""
            [
              {"type":"text","text":"Let me look that up."},
              {"type":"tool_use","id":"toolu_01","name":"lookup_knowledge","input":{"query":"CS0246"}}
            ]
            """, stopReason: "tool_use"));

        var result = await engine.DecideAsync(Envelope());

        result.AssistantText.Should().Be("Let me look that up.");
        result.StopReason.Should().Be("tool_use");
        var call = result.ToolCalls.Should().ContainSingle().Subject;
        call.Id.Should().Be("toolu_01");
        call.Name.Should().Be("lookup_knowledge");
        call.Input.GetProperty("query").GetString().Should().Be("CS0246");
        using (var content = JsonDocument.Parse(result.ContentJson!))
            content.RootElement.EnumerateArray().Select(b => b.GetProperty("type").GetString()).Should().Equal("text", "tool_use");

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        root.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("auto");
        root.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("medium");
        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("adaptive");
        var tool = root.GetProperty("tools").EnumerateArray().Single();
        tool.GetProperty("name").GetString().Should().Be("lookup_knowledge");
        tool.GetProperty("strict").GetBoolean().Should().BeTrue();
        tool.GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral", "the last tool carries a cache breakpoint");
        var inputSchema = tool.GetProperty("input_schema");
        inputSchema.GetProperty("type").GetString().Should().Be("object");
        inputSchema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        inputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("query");
    }

    [Fact]
    public async Task System_blocks_carry_cache_control_and_the_stage_instruction_travels_inside_messages()
    {
        var (engine, handler) = Build(Response("""[{"type":"text","text":"ok"}]"""));

        await engine.DecideAsync(Envelope());

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        var system = root.GetProperty("system");
        system.ValueKind.Should().Be(JsonValueKind.Array);
        var block = system.EnumerateArray().Single();
        block.GetProperty("text").GetString().Should().Be("You are a tutor.");
        block.GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");
        system.GetRawText().Should().NotContain("Produce the plan now.", "the volatile instruction must not touch the cached system field");

        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        messages.Select(m => m.GetProperty("role").GetString()).Should().Equal("user", "system");
        messages[1].GetProperty("content").EnumerateArray().Single().GetProperty("text").GetString().Should().Be("Produce the plan now.");
    }

    [Fact]
    public async Task JudgeAsync_inserts_the_rubric_before_the_trailing_stage_instruction()
    {
        var (engine, handler) = Build(Response("""[{"type":"text","text":"{\"passed\":true,\"score\":0.9,\"rationale\":\"fine\"}"}]"""));

        var result = await engine.JudgeAsync(Envelope(), "Must cite the knowledge base.");

        result.Passed.Should().BeTrue();
        result.Score.Should().Be(0.9);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var roles = body.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()).ToList();
        roles.Should().Equal("user", "user", "system");
        body.RootElement.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("none");
    }

    [Fact]
    public async Task Thinking_is_requested_summarized_and_the_summary_stop_reason_effort_and_latency_are_returned_as_metadata()
    {
        var (engine, handler) = Build(Response("""
            [
              {"type":"thinking","thinking":"The learner hit CS0246, so a knowledge lookup comes first.","signature":"sig_01"},
              {"type":"text","text":"Let me look that up."},
              {"type":"tool_use","id":"toolu_01","name":"lookup_knowledge","input":{"query":"CS0246"}}
            ]
            """, stopReason: "tool_use"));

        var result = await engine.DecideAsync(Envelope());

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var thinking = body.RootElement.GetProperty("thinking");
        thinking.GetProperty("type").GetString().Should().Be("adaptive");
        thinking.GetProperty("display").GetString().Should().Be("summarized", "the Evidence Plane stores the provider's reasoning summary");

        var metadata = result.Metadata.Should().NotBeNull().And.Subject.As<ModelCallMetadata>();
        metadata.ReasoningSummary.Should().Be("The learner hit CS0246, so a knowledge lookup comes first.");
        metadata.StopReason.Should().Be("tool_use");
        metadata.Effort.Should().Be("medium");
        metadata.LatencyMs.Should().BeGreaterThanOrEqualTo(0);
        result.AssistantText.Should().Be("Let me look that up.", "thinking blocks are not assistant text");
        result.ContentJson.Should().Contain("\"type\":\"thinking\"", "the thinking block is replayed verbatim on later turns");
    }

    [Fact]
    public async Task Plan_metadata_reports_high_effort_and_no_summary_when_thinking_is_omitted()
    {
        var planJson = """{"goal":"Explain CS0246","steps":[{"index":0,"description":"Look it up","tool":"lookup_knowledge","success_criteria":"entry found"}]}""";
        var (engine, _) = Build(Response($$"""[{"type":"thinking","thinking":"","signature":"sig"},{"type":"text","text":{{JsonSerializer.Serialize(planJson)}}}]"""));

        var result = await engine.PlanAsync(Envelope());

        result.Succeeded.Should().BeTrue();
        result.Metadata!.Effort.Should().Be("high");
        result.Metadata.StopReason.Should().Be("end_turn");
        result.Metadata.ReasoningSummary.Should().BeNull("an empty thinking block (display omitted) leaves no summary");
    }

    [Fact]
    public async Task Refusal_stop_reason_throws_AIRefusalException()
    {
        var (engine, _) = Build("""
            {
              "id": "msg_02", "type": "message", "role": "assistant", "model": "claude-opus-5-5",
              "content": [], "stop_reason": "refusal", "stop_sequence": null,
              "stop_details": { "type": "refusal", "category": "cyber", "explanation": "Declined." },
              "usage": { "input_tokens": 10, "output_tokens": 0 }
            }
            """);

        var act = () => engine.DecideAsync(Envelope());

        (await act.Should().ThrowAsync<AIRefusalException>()).Which.Category.Should().Be("cyber");
    }
}
