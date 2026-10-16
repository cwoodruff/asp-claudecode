using System.Text;
using System.Text.Json;
using Intelligently.Core.Agents.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Intelligently.Infrastructure.Agents.Execution.Mcp;

/// <summary>
/// One tool of a connected MCP server, registered under <c>server{separator}tool</c>. The call's input
/// object is forwarded as the tool arguments; the result's text blocks become <see cref="ToolResult.Content"/>,
/// its structured content becomes <see cref="ToolResult.Payload"/>, and the server's <c>isError</c> flag
/// becomes <see cref="ToolResult.IsError"/>.
/// </summary>
public sealed class McpToolHandler : IToolHandler
{
    private readonly McpClient _client;
    private readonly McpClientTool _tool;
    private readonly TimeSpan _callTimeout;

    public McpToolHandler(McpClient client, McpClientTool tool, string registeredName, ToolRiskLevel riskLevel, TimeSpan callTimeout)
    {
        _client = client;
        _tool = tool;
        _callTimeout = callTimeout;

        Descriptor = new ToolDescriptor(
            registeredName,
            tool.Description ?? $"MCP tool '{tool.Name}'.",
            tool.JsonSchema.Clone(),
            riskLevel,
            CourseScopeArgument: null,
            OutputSchema: tool.ReturnJsonSchema?.Clone(),
            // MCP schemas rarely close every object and require every property, which strict mode demands.
            Strict: false);
    }

    public ToolDescriptor Descriptor { get; }

    /// <summary>The tool's name on its server (without the namespace prefix).</summary>
    public string RemoteName => _tool.Name;

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct = default)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (call.Input.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in call.Input.EnumerateObject())
                arguments[property.Name] = property.Value.Clone();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_callTimeout);

        CallToolResult response;
        try
        {
            response = await _client.CallToolAsync(_tool.Name, arguments, progress: null, options: null, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Error(call, $"MCP tool '{Descriptor.Name}' did not answer within {_callTimeout.TotalSeconds:0}s.");
        }

        return Map(call, response);
    }

    /// <summary>Maps an MCP result to the normalized <see cref="ToolResult"/> shape.</summary>
    public static ToolResult Map(ToolCall call, CallToolResult response)
    {
        var text = new StringBuilder();
        foreach (var block in response.Content)
        {
            switch (block)
            {
                case TextContentBlock textBlock:
                    if (text.Length > 0) text.Append('\n');
                    text.Append(textBlock.Text);
                    break;
                default:
                    if (text.Length > 0) text.Append('\n');
                    text.Append('[').Append(block.Type).Append(" content omitted]");
                    break;
            }
        }

        JsonElement? payload = null;
        if (response.StructuredContent is { } structured)
        {
            payload = JsonSerializer.SerializeToElement(structured);
            if (text.Length == 0) text.Append(payload.Value.GetRawText());
        }

        var isError = response.IsError == true;
        return new ToolResult(
            call.Id,
            call.Name,
            isError,
            text.Length == 0 ? (isError ? "The MCP tool reported an error without details." : string.Empty) : text.ToString(),
            isError ? ToolResult.ErrorKindToolFailure : null,
            payload);
    }
}
