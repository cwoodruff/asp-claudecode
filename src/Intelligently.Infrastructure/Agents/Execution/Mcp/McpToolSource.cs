using System.Text.RegularExpressions;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Intelligently.Infrastructure.Agents.Execution.Mcp;

/// <summary>
/// Connects to the MCP servers under <c>Agents:Mcp:Servers</c>, lists their tools and exposes one
/// <see cref="McpToolHandler"/> per tool named <c>server{separator}tool</c> (plan section 1.3). Every tool
/// is <see cref="ToolRiskLevel.External"/> unless <c>Agents:Mcp:RiskOverrides</c> says otherwise. A server
/// that cannot be reached is logged and skipped; it never stops the host. Connections are shared for
/// the lifetime of the host and closed on shutdown.
/// </summary>
public sealed partial class McpToolSource : IToolSource, IAsyncDisposable, IDisposable
{
    private readonly AgentsOptions.McpOptions _options;
    private readonly IMcpTransportFactory _transports;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpToolSource> _logger;
    private readonly List<McpClient> _clients = [];
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private IReadOnlyList<IToolHandler> _handlers = [];
    private bool _connected;

    public McpToolSource(IOptions<AgentsOptions> options, ILoggerFactory loggerFactory, IMcpTransportFactory? transports = null)
    {
        _options = options.Value.Mcp;
        _transports = transports ?? new ConfiguredMcpTransportFactory();
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<McpToolSource>();
    }

    public IReadOnlyList<IToolHandler> Handlers => _handlers;

    /// <summary>Names of the servers that connected successfully.</summary>
    public IReadOnlyList<string> ConnectedServers { get; private set; } = [];

    /// <summary>Connects every configured server once; later calls are no-ops.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            if (_connected) return;

            var handlers = new List<IToolHandler>();
            var connected = new List<string>();
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var server in _options.Servers)
            {
                var serverName = Sanitize(server.Name);
                if (serverName.Length == 0)
                {
                    _logger.LogWarning("Skipping an MCP server entry without a name.");
                    continue;
                }

                var client = await TryConnectAsync(server, ct);
                if (client is null) continue;

                IList<McpClientTool> tools;
                try
                {
                    tools = await client.ListToolsAsync(cancellationToken: ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "MCP server {Server}: listing tools failed; the server is skipped", server.Name);
                    await client.DisposeAsync();
                    continue;
                }

                _clients.Add(client);
                connected.Add(server.Name);

                foreach (var tool in tools)
                {
                    var registeredName = $"{serverName}{_options.NameSeparator}{Sanitize(tool.Name)}";
                    if (!names.Add(registeredName))
                    {
                        _logger.LogWarning("MCP server {Server}: tool {Tool} maps to the already registered name {Name}; skipped", server.Name, tool.Name, registeredName);
                        continue;
                    }

                    var risk = RiskLevelFor(registeredName, tool.Name);
                    handlers.Add(new McpToolHandler(client, tool, registeredName, risk, TimeSpan.FromSeconds(Math.Max(1, _options.CallTimeoutSeconds))));
                    _logger.LogInformation("MCP server {Server}: registered tool {Name} as {Risk}", server.Name, registeredName, risk);
                }
            }

            _handlers = handlers;
            ConnectedServers = connected;
            _connected = true;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<McpClient?> TryConnectAsync(AgentsOptions.McpServerOptions server, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ConnectTimeoutSeconds)));
        try
        {
            var transport = _transports.Create(server);
            return await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("MCP server {Server}: connection timed out after {Seconds}s; the server is skipped", server.Name, _options.ConnectTimeoutSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MCP server {Server}: connection failed; the server is skipped", server.Name);
            return null;
        }
    }

    private ToolRiskLevel RiskLevelFor(string registeredName, string toolName)
    {
        if (_options.RiskOverrides.TryGetValue(registeredName, out var byRegisteredName)) return byRegisteredName;
        if (_options.RiskOverrides.TryGetValue(toolName, out var byToolName)) return byToolName;
        return ToolRiskLevel.External;
    }

    /// <summary>Keeps only the characters the provider accepts in tool names.</summary>
    public static string Sanitize(string name) => DisallowedCharacters().Replace(name.Trim(), "_");

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex DisallowedCharacters();

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            try { await client.DisposeAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Disposing an MCP client failed"); }
        }
        _clients.Clear();
        _connectLock.Dispose();
    }

    /// <summary>Synchronous disposal for containers that are disposed synchronously (test hosts); blocks briefly on the clients.</summary>
    public void Dispose()
    {
        try { DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Disposing the MCP tool source failed"); }
    }
}

/// <summary>Connects the MCP servers at host start, before the run worker takes its first run, and closes them at stop.</summary>
public sealed class McpToolSourceHostedService : IHostedService
{
    private readonly McpToolSource _source;

    public McpToolSourceHostedService(McpToolSource source)
    {
        _source = source;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _source.ConnectAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => _source.DisposeAsync().AsTask();
}
