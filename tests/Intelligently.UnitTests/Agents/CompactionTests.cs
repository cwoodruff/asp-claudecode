using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.UnitTests.AI;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// Server-side compaction: the beta header and edit are sent when the envelope asks for it, a
/// <c>compaction</c> stop continues the turn with the full response content appended, and a compaction
/// block stored from one response is sent back verbatim on the next request.
/// </summary>
public class CompactionTests
{
    private const string CompactionResponse = """
        {
          "id": "msg_c1", "type": "message", "role": "assistant", "model": "claude-opus-5-5",
          "content": [{"type":"compaction","content":"The learner asked about CS0246; nothing was looked up yet.","encrypted_content":"enc-abc","signature":"sig-abc"}],
          "stop_reason": "compaction", "stop_sequence": null,
          "usage": { "input_tokens": 120000, "output_tokens": 80, "cache_read_input_tokens": 100000, "cache_creation_input_tokens": 0 }
        }
        """;

    private static readonly string ToolUseResponse = AnthropicReasoningEngineTests.Response("""
        [
          {"type":"text","text":"Continuing after compaction."},
          {"type":"tool_use","id":"toolu_02","name":"lookup_knowledge","input":{"query":"CS0246"}}
        ]
        """, stopReason: "tool_use");

    [Fact]
    public async Task Compaction_stop_continues_the_turn_with_the_full_response_content_appended()
    {
        var bodies = new List<string>();
        var headers = new List<string?>();
        var calls = 0;
        var (engine, _) = AnthropicReasoningEngineTests.Build(request =>
        {
            calls++;
            bodies.Add(request.Content!.ReadAsStringAsync().Result);
            headers.Add(request.Headers.TryGetValues("anthropic-beta", out var values) ? string.Join(",", values) : null);
            return FakeAnthropicHandler.Json(calls == 1 ? CompactionResponse : ToolUseResponse);
        });
        var envelope = AnthropicReasoningEngineTests.Envelope() with { Compaction = true };

        var result = await engine.DecideAsync(envelope);

        calls.Should().Be(2);
        headers.Should().OnlyContain(h => h != null && h.Contains("compact-2026-01-12"));

        using var first = JsonDocument.Parse(bodies[0]);
        var edit = first.RootElement.GetProperty("context_management").GetProperty("edits").EnumerateArray().Single();
        edit.GetProperty("type").GetString().Should().Be("compact_20260112");
        edit.GetProperty("trigger").GetProperty("type").GetString().Should().Be("input_tokens");
        edit.GetProperty("trigger").GetProperty("value").GetInt64().Should().Be(100_000);

        using var second = JsonDocument.Parse(bodies[1]);
        var messages = second.RootElement.GetProperty("messages").EnumerateArray().ToList();
        messages.Select(m => m.GetProperty("role").GetString()).Should().Equal("user", "system", "assistant");
        var appended = messages[2].GetProperty("content").EnumerateArray().Single();
        appended.GetProperty("type").GetString().Should().Be("compaction");
        appended.GetProperty("encrypted_content").GetString().Should().Be("enc-abc");
        appended.GetProperty("signature").GetString().Should().Be("sig-abc");

        result.ToolCalls.Should().ContainSingle().Which.Id.Should().Be("toolu_02");
        result.StopReason.Should().Be("tool_use");
        result.Usage.InputTokens.Should().Be(120_300, "usage is summed across the continuation");
        using var content = JsonDocument.Parse(result.ContentJson!);
        content.RootElement.EnumerateArray().Select(b => b.GetProperty("type").GetString()).Should().Equal("compaction", "text", "tool_use");
    }

    [Fact]
    public async Task Compaction_block_stored_from_a_response_is_preserved_verbatim_in_the_next_request()
    {
        // Turn 1: the mocked response carries a compaction block; the engine hands it back as ContentJson.
        var (first, _) = AnthropicReasoningEngineTests.Build(_ => FakeAnthropicHandler.Json(AnthropicReasoningEngineTests.Response(
            """[{"type":"compaction","content":"Summary so far.","encrypted_content":"enc-xyz","signature":"sig-xyz"},{"type":"text","text":"Done."}]""")));
        var turn1 = await first.DecideAsync(AnthropicReasoningEngineTests.Envelope());

        // Turn 2: the assembler replays the stored content as raw blocks in the transcript (as ContextAssembler does).
        using var stored = JsonDocument.Parse(turn1.ContentJson!);
        var replayed = new PromptMessage(PromptMessage.AssistantRole, stored.RootElement.EnumerateArray().Select(PromptContent.FromRaw).ToList());
        var envelope = AnthropicReasoningEngineTests.Envelope();
        var nextTurn = envelope with
        {
            Compaction = true,
            Messages =
            [
                envelope.Messages[0],
                replayed,
                new PromptMessage(PromptMessage.UserRole, "Plan recorded."),
                envelope.Messages[1],
            ],
        };
        var (second, handler) = AnthropicReasoningEngineTests.Build(_ => FakeAnthropicHandler.Json(ToolUseResponse));

        await second.DecideAsync(nextTurn);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var assistant = body.RootElement.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant");
        var blocks = assistant.GetProperty("content").EnumerateArray().ToList();
        blocks.Select(b => b.GetProperty("type").GetString()).Should().Equal("compaction", "text");
        blocks[0].GetProperty("content").GetString().Should().Be("Summary so far.");
        blocks[0].GetProperty("encrypted_content").GetString().Should().Be("enc-xyz");
        blocks[0].GetProperty("signature").GetString().Should().Be("sig-xyz");
        handler.LastRequest!.Headers.GetValues("anthropic-beta").Should().Contain(v => v.Contains("compact-2026-01-12"));
    }

    [Fact]
    public async Task Without_compaction_no_beta_header_or_context_management_is_sent()
    {
        var (engine, handler) = AnthropicReasoningEngineTests.Build(_ => FakeAnthropicHandler.Json(ToolUseResponse));

        await engine.DecideAsync(AnthropicReasoningEngineTests.Envelope());

        handler.LastRequest!.Headers.Contains("anthropic-beta").Should().BeFalse();
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.TryGetProperty("context_management", out _).Should().BeFalse();
    }
}
