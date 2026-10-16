using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Execution.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// <see cref="McpToolSource"/> against an in-process MCP server: tools are listed and registered under
/// the server prefix, risk defaults to External unless overridden, results map text, structured content
/// and <c>isError</c>, and an unreachable server is skipped with a warning rather than failing the host.
/// </summary>
public class McpToolSourceTests
{
    private static AgentsOptions.McpServerOptions Server(string name) => new() { Name = name, Transport = "stdio", Command = "unused" };

    private static McpToolSource Source(IMcpTransportFactory transports, ILoggerFactory? logging = null, params (string Tool, ToolRiskLevel Risk)[] overrides)
    {
        var options = new AgentsOptions();
        options.Mcp.Servers.Add(Server("stub"));
        foreach (var (tool, risk) in overrides) options.Mcp.RiskOverrides[tool] = risk;
        return new McpToolSource(Options.Create(options), logging ?? NullLoggerFactory.Instance, transports);
    }

    private static ToolCall Call(string name, object input, string id = "toolu_mcp") => new(id, name, JsonSerializer.SerializeToElement(input));

    [Fact]
    public async Task Lists_the_server_tools_under_the_server_prefix_with_External_risk_unless_overridden()
    {
        await using var server = InProcessMcpServer.Stub();
        await using var source = Source(server, overrides: [("stub__echo", ToolRiskLevel.ReadOnly), ("double", ToolRiskLevel.Mutating)]);

        await source.ConnectAsync();
        await source.ConnectAsync();

        server.Connections.Should().Be(1, "a second ConnectAsync is a no-op");
        source.ConnectedServers.Should().Equal("stub");
        var descriptors = source.Handlers.Select(h => h.Descriptor).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        descriptors.Select(d => d.Name).Should().Equal("stub__crash", "stub__double", "stub__echo", "stub__fail");
        descriptors.Single(d => d.Name == "stub__echo").RiskLevel.Should().Be(ToolRiskLevel.ReadOnly, "overridden by registered name");
        descriptors.Single(d => d.Name == "stub__double").RiskLevel.Should().Be(ToolRiskLevel.Mutating, "overridden by bare tool name");
        descriptors.Single(d => d.Name == "stub__fail").RiskLevel.Should().Be(ToolRiskLevel.External, "the default");
        descriptors.Should().OnlyContain(d => !d.Strict, "MCP schemas are not strict-mode schemas");
        var echo = descriptors.Single(d => d.Name == "stub__echo");
        echo.Description.Should().Be("Echoes the text back.");
        echo.InputSchema.GetProperty("properties").TryGetProperty("text", out _).Should().BeTrue();
        descriptors.Single(d => d.Name == "stub__double").OutputSchema.Should().NotBeNull("the server declares structured output");
    }

    [Fact]
    public async Task Results_map_text_structured_content_and_is_error()
    {
        await using var server = InProcessMcpServer.Stub();
        await using var source = Source(server);
        await source.ConnectAsync();
        var handlers = source.Handlers.ToDictionary(h => h.Descriptor.Name);
        var context = StubTool.Context();

        var echoed = await handlers["stub__echo"].ExecuteAsync(Call("stub__echo", new { text = "ping" }), context);
        echoed.IsError.Should().BeFalse();
        echoed.Content.Should().Be("echo: ping");

        var doubled = await handlers["stub__double"].ExecuteAsync(Call("stub__double", new { count = 21 }), context);
        doubled.IsError.Should().BeFalse();
        doubled.Payload.Should().NotBeNull();
        doubled.Payload!.Value.GetProperty("doubled").GetInt32().Should().Be(42);

        var failed = await handlers["stub__fail"].ExecuteAsync(Call("stub__fail", new { reason = "nope" }), context);
        failed.IsError.Should().BeTrue();
        failed.ErrorKind.Should().Be(ToolResult.ErrorKindToolFailure);
        failed.Content.Should().Be("failed: nope");

        var crashed = await handlers["stub__crash"].ExecuteAsync(Call("stub__crash", new { reason = "boom" }), context);
        crashed.IsError.Should().BeTrue("a tool that throws on the server comes back as an error result, not an exception");
    }

    [Fact]
    public async Task Unreachable_server_is_logged_and_skipped_without_throwing()
    {
        var logs = new List<string>();
        using var logging = LoggerFactory.Create(b => b.AddProvider(new ListLoggerProvider(logs)));
        await using var source = Source(new BrokenMcpTransportFactory(), logging);

        var act = () => source.ConnectAsync();

        await act.Should().NotThrowAsync();
        source.Handlers.Should().BeEmpty();
        source.ConnectedServers.Should().BeEmpty();
        logs.Should().ContainSingle(l => l.Contains("Warning") && l.Contains("stub") && l.Contains("skipped"));
    }

    [Fact]
    public async Task Mcp_tools_dispatch_through_the_registry_and_the_control_plane()
    {
        await using var server = InProcessMcpServer.Stub();
        using var host = new AgentTestHost(services =>
        {
            services.RemoveAll<IMcpTransportFactory>();
            services.AddSingleton<IMcpTransportFactory>(server);
            services.Configure<AgentsOptions>(o =>
            {
                o.Mcp.Servers.Add(Server("stub"));
                o.Mcp.RiskOverrides["stub__echo"] = ToolRiskLevel.ReadOnly;
            });
        });
        await host.Provider.GetRequiredService<McpToolSource>().ConnectAsync();
        var run = await host.CreateRunAsync();

        using var scope = host.Provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IToolRegistry>().Descriptors.Select(d => d.Name)
            .Should().Contain(["stub__echo", "stub__fail"]).And.Contain("lookup_knowledge");
        var dispatcher = scope.ServiceProvider.GetRequiredService<IToolDispatcher>();

        var allowed = await dispatcher.DispatchAsync(run, [Call("stub__echo", new { text = "hi" }, "toolu_1"), Call("stub__fail", new { reason = "x" }, "toolu_2")]);
        allowed.RequiresApproval.Should().BeTrue("stub__fail is External and needs approval");
        allowed.PendingCall!.Name.Should().Be("stub__fail");

        var echoed = await dispatcher.DispatchAsync(run, [Call("stub__echo", new { text = "hi" }, "toolu_1")]);
        echoed.RequiresApproval.Should().BeFalse();
        echoed.Results.Should().ContainSingle().Which.Content.Should().Be("echo: hi");
    }

    [Fact]
    public void Sanitize_keeps_only_provider_safe_characters()
    {
        McpToolSource.Sanitize("my server.v2/tools").Should().Be("my_server_v2_tools");
        McpToolSource.Sanitize("  echo-text_1 ").Should().Be("echo-text_1");
    }

    private sealed class ListLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(sink);
        public void Dispose() { }

        private sealed class ListLogger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (sink) sink.Add($"{logLevel}: {formatter(state, exception)}");
            }
        }
    }
}
