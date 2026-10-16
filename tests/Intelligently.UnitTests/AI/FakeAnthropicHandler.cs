using System.Net;
using System.Text;
using System.Text.Json;

namespace Intelligently.UnitTests.AI;

/// <summary>
/// Captures the outgoing request and returns a canned HTTP response, so the real
/// SDK request/response pipeline runs without touching the network.
/// </summary>
internal sealed class FakeAnthropicHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public FakeAnthropicHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }
    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return _responder(request);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Builds a text/event-stream body from JSON event payloads (event name taken from "type").</summary>
    public static HttpResponseMessage Sse(params string[] events)
    {
        var sb = new StringBuilder();
        foreach (var json in events)
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();
            sb.Append("event: ").Append(type).Append('\n');
            sb.Append("data: ").Append(json).Append("\n\n");
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream"),
        };
    }
}
