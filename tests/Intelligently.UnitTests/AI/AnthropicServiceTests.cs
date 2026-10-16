using System.Text.Json;
using Anthropic;
using FluentAssertions;
using Intelligently.Core.Exceptions;
using Intelligently.Core.Interfaces;
using Intelligently.Infrastructure.AI;

namespace Intelligently.UnitTests.AI;

public class AnthropicServiceTests
{
    private static (AnthropicService Service, FakeAnthropicHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeAnthropicHandler(responder);
        var client = new AnthropicClient
        {
            ApiKey = "test-key",
            HttpClient = new HttpClient(handler),
            MaxRetries = 0,
        };
        return (new AnthropicService(client), handler);
    }

    private static PromptRequest Request(string? model = null) => new(
        SystemPrompt: "You are a tutor.",
        Messages: [("user", "What is 2 + 2?")],
        Model: model,
        MaxOutputTokens: 256);

    private const string NormalResponse = """
        {
          "id": "msg_01",
          "type": "message",
          "role": "assistant",
          "model": "claude-opus-5-5",
          "content": [
            { "type": "text", "text": "2 + 2 = " },
            { "type": "text", "text": "4." }
          ],
          "stop_reason": "end_turn",
          "stop_sequence": null,
          "usage": { "input_tokens": 12, "output_tokens": 6 }
        }
        """;

    private const string RefusalResponse = """
        {
          "id": "msg_02",
          "type": "message",
          "role": "assistant",
          "model": "claude-opus-5-5",
          "content": [],
          "stop_reason": "refusal",
          "stop_sequence": null,
          "stop_details": {
            "type": "refusal",
            "category": "cyber",
            "explanation": "This request was declined by a safety classifier."
          },
          "usage": { "input_tokens": 12, "output_tokens": 0 }
        }
        """;

    [Fact]
    public async Task GetCompletionAsync_ReturnsConcatenatedText_ForNormalResponse()
    {
        var (service, handler) = Build(_ => FakeAnthropicHandler.Json(NormalResponse));

        var text = await service.GetCompletionAsync(Request());

        text.Should().Be("2 + 2 = 4.");
        handler.CallCount.Should().Be(1);
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().EndWith("/v1/messages");
    }

    [Fact]
    public async Task GetCompletionAsync_SendsDefaultModelSystemAndAdaptiveThinking()
    {
        var (service, handler) = Build(_ => FakeAnthropicHandler.Json(NormalResponse));

        await service.GetCompletionAsync(Request());

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        root.GetProperty("model").GetString().Should().Be(AnthropicService.DefaultModel);
        root.GetProperty("max_tokens").GetInt32().Should().Be(256);
        root.GetProperty("system").GetString().Should().Be("You are a tutor.");
        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("adaptive");
        root.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("user");
        root.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("What is 2 + 2?");
    }

    [Fact]
    public async Task GetCompletionAsync_HonoursExplicitModel()
    {
        var (service, handler) = Build(_ => FakeAnthropicHandler.Json(NormalResponse));

        await service.GetCompletionAsync(Request(model: AnthropicService.CheapModel));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("model").GetString().Should().Be("claude-haiku-5-5");
    }

    [Fact]
    public async Task GetCompletionAsync_ThrowsAIRefusalException_OnRefusalStopReason()
    {
        var (service, _) = Build(_ => FakeAnthropicHandler.Json(RefusalResponse));

        var act = () => service.GetCompletionAsync(Request());

        var ex = await act.Should().ThrowAsync<AIRefusalException>();
        ex.Which.Category.Should().Be("cyber");
        ex.Which.Explanation.Should().Contain("safety classifier");
        ex.Which.Message.Should().Contain("refused");
    }

    [Fact]
    public async Task StreamCompletionAsync_YieldsTextDeltas_ForNormalStream()
    {
        var (service, handler) = Build(_ => FakeAnthropicHandler.Sse(
            """{"type":"message_start","message":{"id":"msg_03","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":12,"output_tokens":1}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"2 + 2 "}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"= 4."}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":6}}""",
            """{"type":"message_stop"}"""));

        var chunks = new List<string>();
        await foreach (var chunk in service.StreamCompletionAsync(Request()))
            chunks.Add(chunk);

        chunks.Should().Equal("2 + 2 ", "= 4.");
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("model").GetString().Should().Be(AnthropicService.DefaultModel);
    }

    [Fact]
    public async Task StreamCompletionAsync_ThrowsAIRefusalException_OnRefusalStopReason()
    {
        var (service, _) = Build(_ => FakeAnthropicHandler.Sse(
            """{"type":"message_start","message":{"id":"msg_04","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":12,"output_tokens":0}}}""",
            """{"type":"message_delta","delta":{"stop_reason":"refusal","stop_sequence":null,"stop_details":{"type":"refusal","category":"cyber","explanation":"Declined."}},"usage":{"output_tokens":0}}""",
            """{"type":"message_stop"}"""));

        var act = async () =>
        {
            await foreach (var _ in service.StreamCompletionAsync(Request())) { }
        };

        var ex = await act.Should().ThrowAsync<AIRefusalException>();
        ex.Which.Category.Should().Be("cyber");
    }
}
