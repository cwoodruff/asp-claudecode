using System.IO.Pipelines;
using System.Text.Json;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Execution.Mcp;
using Intelligently.Infrastructure.Agents.Execution.Sandboxes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Intelligently.UnitTests.Agents;

/// <summary>A scripted <see cref="ISandbox"/>: returns canned results and records every request.</summary>
public sealed class FakeSandbox : ISandbox
{
    private readonly Func<SandboxRequest, SandboxResult> _run;

    public FakeSandbox(Func<SandboxRequest, SandboxResult>? run = null)
    {
        _run = run ?? (r => Completed(r, 0, "ok"));
    }

    public string Name => "fake";
    public List<SandboxRequest> Requests { get; } = [];

    public Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(_run(request));
    }

    public static SandboxResult Completed(SandboxRequest request, int exitCode, string stdout, string stderr = "") =>
        new("fake", exitCode, stdout, stderr, false, false, TimeSpan.FromMilliseconds(42));

    public static SandboxResult TimedOut(SandboxRequest request) =>
        new("fake", null, "partial output", string.Empty, true, false, request.Timeout ?? TimeSpan.FromSeconds(60));

    public static SandboxResult MemoryKilled(SandboxRequest request) =>
        new("fake", 137, string.Empty, "Killed", false, true, TimeSpan.FromSeconds(1));
}

/// <summary>Arguments for one <c>run_lab_code</c> call.</summary>
internal static class LabCode
{
    public static ToolCall Call(string id = "toolu_lab", string language = "csharp", string entrypoint = "Program.cs", string? expectedOutput = null, string content = "System.Console.WriteLine(\"hi\");") =>
        new(id, "run_lab_code", JsonSerializer.SerializeToElement(new
        {
            language,
            files = new[] { new { path = entrypoint, content } },
            entrypoint,
            expected_output = expectedOutput,
        }));
}

/// <summary>
/// An MCP server hosted in-process over a pair of pipes, so <see cref="McpToolSource"/> connects to it
/// through a real client transport without a child process or a socket.
/// </summary>
public sealed class InProcessMcpServer : IMcpTransportFactory, IAsyncDisposable
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly McpServer _server;
    private readonly Task _run;

    public InProcessMcpServer(string serverName, params McpServerTool[] tools)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = serverName, Version = "1.0" },
            ToolCollection = [.. tools],
        };
        var transport = new StreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream(), serverName, NullLoggerFactory.Instance);
        _server = McpServer.Create(transport, options, NullLoggerFactory.Instance, null);
        _run = _server.RunAsync(CancellationToken.None);
    }

    public int Connections { get; private set; }

    public IClientTransport Create(AgentsOptions.McpServerOptions server)
    {
        Connections++;
        return new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
    }

    /// <summary>A server with an <c>echo</c> tool, a <c>fail</c> tool that returns an error, and a <c>crash</c> tool that throws.</summary>
    public static InProcessMcpServer Stub(string serverName = "stub") => new(
        serverName,
        McpServerTool.Create((string text) => $"echo: {text}", new McpServerToolCreateOptions { Name = "echo", Description = "Echoes the text back." }),
        McpServerTool.Create((int count) => new { count, doubled = count * 2 }, new McpServerToolCreateOptions { Name = "double", Description = "Doubles a number.", UseStructuredContent = true }),
        McpServerTool.Create((string reason) => new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"failed: {reason}" }],
        }, new McpServerToolCreateOptions { Name = "fail", Description = "Always reports an error." }),
        McpServerTool.Create(new Func<string, string>(reason => throw new InvalidOperationException($"crashed: {reason}")), new McpServerToolCreateOptions { Name = "crash", Description = "Always throws." }));

    public async ValueTask DisposeAsync()
    {
        await _clientToServer.Writer.CompleteAsync();
        await _serverToClient.Writer.CompleteAsync();
        try { await _server.DisposeAsync(); } catch { /* the pipes are closed */ }
        try { await _run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* the server loop ends with the transport */ }
    }
}

/// <summary>A transport factory whose transports never connect, for the skipped-server path.</summary>
public sealed class BrokenMcpTransportFactory : IMcpTransportFactory
{
    public IClientTransport Create(AgentsOptions.McpServerOptions server) =>
        throw new InvalidOperationException($"server '{server.Name}' is unreachable");
}

/// <summary>Probes the local Docker engine once per test run so Docker-backed tests skip cleanly without it.</summary>
public static class DockerAvailability
{
    private static readonly Lazy<string?> Unavailable = new(Probe);

    /// <summary>Null when Docker answers a ping, otherwise the reason it does not.</summary>
    public static string? SkipReason => Unavailable.Value;

    private static string? Probe()
    {
        try
        {
            using var client = DockerSandbox.CreateClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            client.System.PingAsync(timeout.Token).GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex)
        {
            return $"Docker is not available: {ex.GetType().Name}: {ex.Message}";
        }
    }
}

/// <summary>A fact that is skipped when no Docker engine answers.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (DockerAvailability.SkipReason is { } reason) Skip = reason;
    }
}
