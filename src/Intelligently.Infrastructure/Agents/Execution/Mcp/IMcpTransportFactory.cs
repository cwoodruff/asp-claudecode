using Intelligently.Core.Agents;
using ModelContextProtocol.Client;

namespace Intelligently.Infrastructure.Agents.Execution.Mcp;

/// <summary>Builds the client transport for one configured MCP server. Tests substitute an in-process transport.</summary>
public interface IMcpTransportFactory
{
    IClientTransport Create(AgentsOptions.McpServerOptions server);
}

/// <summary>Default factory: <c>stdio</c> launches the configured command, <c>http</c> connects to the configured URL.</summary>
public sealed class ConfiguredMcpTransportFactory : IMcpTransportFactory
{
    public IClientTransport Create(AgentsOptions.McpServerOptions server)
    {
        if (server.IsStdio)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
                throw new InvalidOperationException($"MCP server '{server.Name}' uses the stdio transport but has no Command.");

            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command,
                Arguments = server.Args,
                WorkingDirectory = server.WorkingDirectory,
                EnvironmentVariables = server.Environment.Count == 0 ? null : server.Environment,
            });
        }

        if (server.IsHttp)
        {
            if (string.IsNullOrWhiteSpace(server.Url))
                throw new InvalidOperationException($"MCP server '{server.Name}' uses the http transport but has no Url.");

            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url),
                AdditionalHeaders = server.Headers.Count == 0 ? null : server.Headers,
            });
        }

        throw new InvalidOperationException($"MCP server '{server.Name}' has unknown transport '{server.Transport}' (expected stdio or http).");
    }
}
