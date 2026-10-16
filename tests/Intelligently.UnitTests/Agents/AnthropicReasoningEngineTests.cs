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
    private static (AnthropicReasoningEngine Engine, FakeAnthropicHandler Handler) Build(string responseJson)
    {
        var handler = new FakeAnthropicHandler(_ => FakeAnthropicHandler.Json(responseJson));
        var client = new AnthropicClient { ApiKey = "test-key", HttpClient = new HttpClient(handler), MaxRetries = 0 };
        var engine = new AnthropicReasoningEngine(client, Options.Create(new AgentsOptions()));
        return (engine, handler);
    }

    private static readonly ToolDescriptor LookupTool = new(
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

    private static PromptEnvelope Envelope() => new(
        "You are a tutor.",
        [LookupTool],
        [new PromptMessage("user", "Plan it.")]);

    private static string Response(string contentJson, string stopReason = "end_turn") => $$"""
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

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        root.GetProperty("model").GetString().Should().Be("claude-opus-5-5");
        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("adaptive");
        root.GetProperty("system").GetString().Should().Be("You are a tutor.");
        var outputConfig = root.GetProperty("output_config");
        outputConfig.GetProperty("effort").GetString().Should().Be("high");
        var format = outputConfig.GetProperty("format");
        format.GetProperty("type").GetString().Should().Be("json_schema");
        var schema = format.GetProperty("schema");
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("goal", "steps");
        root.TryGetProperty("tools", out _).Should().BeFalse("planning uses structured output, not tools");
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

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        root.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("auto");
        root.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("medium");
        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("adaptive");
        var tool = root.GetProperty("tools").EnumerateArray().Single();
        tool.GetProperty("name").GetString().Should().Be("lookup_knowledge");
        tool.GetProperty("strict").GetBoolean().Should().BeTrue();
        var inputSchema = tool.GetProperty("input_schema");
        inputSchema.GetProperty("type").GetString().Should().Be("object");
        inputSchema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        inputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("query");
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
