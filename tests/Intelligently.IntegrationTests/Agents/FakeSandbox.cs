using Intelligently.Core.Agents.Contracts;

namespace Intelligently.IntegrationTests.Agents;

/// <summary>A scripted <see cref="ISandbox"/> for the Web host: records every request and prints "hi".</summary>
public sealed class FakeSandbox : ISandbox
{
    public string Name => "fake";
    public List<SandboxRequest> Requests { get; } = [];

    public Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct = default)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult(new SandboxResult(Name, 0, "hi\n", string.Empty, false, false, TimeSpan.FromMilliseconds(5)));
    }
}
