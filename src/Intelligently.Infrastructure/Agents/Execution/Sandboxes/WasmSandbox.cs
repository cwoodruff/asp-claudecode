using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasmtime;

namespace Intelligently.Infrastructure.Agents.Execution.Sandboxes;

/// <summary>
/// Runs a WASI command module with Wasmtime (plan section 1.3): sub-second startup for short,
/// deterministic snippets. The first element of the command names the module among the submitted files
/// (binary <c>.wasm</c> bytes, or WAT text); the rest are its arguments. Limits: fuel (instruction budget),
/// an epoch-based wall-clock timeout, and a linear-memory cap. Stdout and stderr are captured to temp
/// files and capped. No host directories are exposed except the request's own files, read-only.
/// </summary>
public sealed partial class WasmSandbox : ISandbox
{
    public const string SandboxName = "wasm";
    private const string EntryFunction = "_start";
    private static readonly TimeSpan EpochTick = TimeSpan.FromMilliseconds(10);

    private readonly AgentsOptions.WasmOptions _options;
    private readonly ILogger<WasmSandbox> _logger;

    public WasmSandbox(IOptions<AgentsOptions> options, ILogger<WasmSandbox> logger)
    {
        _options = options.Value.Sandbox.Wasm;
        _logger = logger;
    }

    public string Name => SandboxName;

    public Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct = default) =>
        Task.Run(() => Run(request, ct), ct);

    private SandboxResult Run(SandboxRequest request, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        if (request.Command.Count == 0)
            return SandboxResult.Failed(Name, "No module given.", watch.Elapsed);

        var moduleFile = request.Files.FirstOrDefault(f => string.Equals(f.Path, request.Command[0], StringComparison.Ordinal));
        if (moduleFile is null)
            return SandboxResult.Failed(Name, $"Module '{request.Command[0]}' is not among the submitted files.", watch.Elapsed);

        var timeout = request.Timeout ?? TimeSpan.FromSeconds(_options.TimeoutSeconds);
        var memory = request.MemoryBytes ?? _options.MemoryBytes;
        var maxOutput = request.MaxOutputBytes ?? _options.MaxOutputBytes;

        var workDir = Path.Combine(Path.GetTempPath(), "intelligently-wasm", Guid.NewGuid().ToString("N"));
        var stdoutPath = Path.Combine(workDir, ".stdout");
        var stderrPath = Path.Combine(workDir, ".stderr");
        try
        {
            Directory.CreateDirectory(workDir);
            SandboxFiles.Write(workDir, request.Files.Where(f => !ReferenceEquals(f, moduleFile)).ToList());
            File.WriteAllBytes(stdoutPath, []);
            File.WriteAllBytes(stderrPath, []);

            using var config = new Config()
                .WithFuelConsumption(true)
                .WithEpochInterruption(true);
            using var engine = new Engine(config);

            Module module;
            try
            {
                module = moduleFile.Bytes is { } bytes
                    ? Module.FromBytes(engine, moduleFile.Path, bytes)
                    : Module.FromText(engine, moduleFile.Path, moduleFile.Content ?? string.Empty);
            }
            catch (WasmtimeException ex)
            {
                return SandboxResult.Failed(Name, $"The module could not be compiled: {ex.Message}", watch.Elapsed);
            }

            int? exitCode;
            var timedOut = false;
            var memoryExceeded = false;
            string? error = null;

            // The store owns the WASI stdout/stderr handles; it is disposed before the files are read.
            using (module)
            using (var linker = new Linker(engine))
            using (var store = new Store(engine))
            {
                linker.DefineWasi();
                store.Fuel = (ulong)Math.Max(1, _options.Fuel);
                store.SetLimits(memorySize: memory, tableElements: null, instances: null, tables: null, memories: null);

                var wasi = new WasiConfiguration()
                    .WithArgs(request.Command.ToArray())
                    .WithStandardOutput(stdoutPath)
                    .WithStandardError(stderrPath)
                    .WithPreopenedDirectory(workDir, "/", true);
                if (request.Environment is not null)
                    wasi = wasi.WithEnvironmentVariables(request.Environment.Select(kv => (kv.Key, kv.Value)));
                store.SetWasiConfiguration(wasi);

                // Epoch deadline: a timer advances the engine epoch every tick; the store traps once the deadline passes.
                var ticks = (ulong)Math.Max(1, (long)Math.Ceiling(timeout.TotalMilliseconds / EpochTick.TotalMilliseconds));
                store.SetEpochDeadline(ticks);
                using var registration = ct.Register(() => store.SetEpochDeadline(0));
                using var timer = new Timer(_ => engine.IncrementEpoch(), null, EpochTick, EpochTick);

                try
                {
                    var instance = linker.Instantiate(store, module);
                    var start = instance.GetAction(EntryFunction);
                    if (start is null)
                        return SandboxResult.Failed(Name, $"The module exports no '{EntryFunction}' function.", watch.Elapsed);

                    start();
                    exitCode = 0;
                }
                catch (TrapException ex)
                {
                    exitCode = null;
                    switch (ex.Type)
                    {
                        case TrapCode.OutOfFuel:
                            timedOut = true;
                            error = "The module exhausted its fuel budget.";
                            break;
                        case TrapCode.Interrupt:
                            timedOut = true;
                            error = ct.IsCancellationRequested ? "The run was cancelled." : $"The module exceeded the {timeout.TotalSeconds:0.##}s limit.";
                            break;
                        case TrapCode.MemoryOutOfBounds:
                        case TrapCode.AllocationTooLarge:
                            memoryExceeded = true;
                            error = $"The module exceeded its memory limit ({memory} bytes) or accessed memory out of bounds.";
                            break;
                        default:
                            error = $"The module trapped: {ex.Type} ({ex.Message}).";
                            break;
                    }
                }
                catch (WasmtimeException ex)
                {
                    var exit = ExitStatus().Match(ex.Message);
                    if (exit.Success)
                    {
                        exitCode = int.Parse(exit.Groups[1].Value);
                    }
                    else
                    {
                        exitCode = null;
                        error = $"The module failed: {ex.Message}";
                    }
                }
            }

            var (stdout, outTruncated) = ReadCapped(stdoutPath, maxOutput);
            var (stderr, errTruncated) = ReadCapped(stderrPath, maxOutput);
            return new SandboxResult(Name, exitCode, stdout, stderr, timedOut, memoryExceeded, watch.Elapsed, outTruncated || errTruncated, error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Wasm sandbox run failed");
            return SandboxResult.Failed(Name, $"{ex.GetType().Name}: {ex.Message}", watch.Elapsed);
        }
        finally
        {
            SandboxFiles.TryDelete(workDir);
        }
    }

    private static (string Text, bool Truncated) ReadCapped(string path, int maxBytes)
    {
        if (!File.Exists(path)) return (string.Empty, false);
        using var stream = File.OpenRead(path);
        var length = stream.Length;
        var take = (int)Math.Min(length, Math.Max(0, maxBytes));
        var bytes = new byte[take];
        stream.ReadExactly(bytes, 0, take);
        var text = Encoding.UTF8.GetString(bytes);
        var truncated = length > take;
        return (truncated ? text + "\n…[output truncated]" : text, truncated);
    }

    [GeneratedRegex(@"exit(?:ed)?(?: with)?(?: i32)?(?: exit)? status (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExitStatus();
}
