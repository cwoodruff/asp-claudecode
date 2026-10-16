namespace Intelligently.Core.Agents.Contracts;

/// <summary>One file placed in the sandbox working directory before the command runs. Text or bytes, never both.</summary>
public sealed record SandboxFile(string Path, string? Content = null, byte[]? Bytes = null);

/// <summary>
/// What to run in a throwaway sandbox. <see cref="Command"/> is the argv: for a container it is the
/// program and its arguments, run in the working directory; for a WebAssembly sandbox the first element
/// names the <c>.wasm</c> module among <see cref="Files"/> (binary, or WAT text) and the rest are its
/// arguments. Null limits fall back to the configured sandbox defaults.
/// </summary>
public sealed record SandboxRequest(IReadOnlyList<string> Command, IReadOnlyList<SandboxFile> Files)
{
    public TimeSpan? Timeout { get; init; }
    public long? MemoryBytes { get; init; }
    public double? Cpus { get; init; }
    public int? MaxOutputBytes { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>
/// Normalized outcome of a sandbox run. <see cref="TimedOut"/> covers wall-clock and fuel/epoch limits,
/// <see cref="MemoryExceeded"/> the memory limit; <see cref="Error"/> is set when the sandbox itself could
/// not run the command (missing image, unreadable module). Captured output is capped and
/// <see cref="OutputTruncated"/> says when the cap was hit.
/// </summary>
public sealed record SandboxResult(
    string Sandbox,
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    bool MemoryExceeded,
    TimeSpan Duration,
    bool OutputTruncated = false,
    string? Error = null)
{
    public bool Succeeded => !TimedOut && !MemoryExceeded && Error is null && ExitCode == 0;

    public static SandboxResult Failed(string sandbox, string error, TimeSpan duration) =>
        new(sandbox, null, string.Empty, string.Empty, false, false, duration, Error: error);
}

/// <summary>Runs untrusted code in isolation: no network, bounded CPU, memory and time, output captured.</summary>
public interface ISandbox
{
    /// <summary>Short name used in results and evidence (<c>docker</c>, <c>wasm</c>, <c>fake</c>).</summary>
    string Name { get; }

    Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct = default);
}
