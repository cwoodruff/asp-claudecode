using Intelligently.Core.Agents.Contracts;

namespace Intelligently.IntegrationTests.Agents;

/// <summary>
/// A scripted <see cref="ISandbox"/> for the Web host: records every request and, unless a test installs a
/// <see cref="Script"/>, prints "hi" and exits 0.
/// </summary>
public sealed class FakeSandbox : ISandbox
{
    public string Name => "fake";
    public List<SandboxRequest> Requests { get; } = [];

    /// <summary>Per-test behaviour; null restores the default "hi" result. Tests that set it must reset it.</summary>
    public Func<SandboxRequest, SandboxResult>? Script { get; set; }

    public Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct = default)
    {
        lock (Requests) Requests.Add(request);
        var script = Script;
        return Task.FromResult(script is null
            ? new SandboxResult(Name, 0, "hi\n", string.Empty, false, false, TimeSpan.FromMilliseconds(5))
            : script(request));
    }
}
