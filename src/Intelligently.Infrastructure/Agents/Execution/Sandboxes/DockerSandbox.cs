using System.Diagnostics;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Execution.Sandboxes;

/// <summary>
/// Runs a command in a throwaway container (plan section 1.3): the configured image, no network, CPU,
/// memory and pid limits, all capabilities dropped, a wall-clock timeout, the submitted files in a temp
/// folder bind-mounted as the working directory, stdout/stderr captured up to a byte cap. The container
/// is always removed, and the temp folder deleted, whatever happens.
/// </summary>
public sealed class DockerSandbox : ISandbox, IDisposable
{
    public const string SandboxName = "docker";

    private readonly AgentsOptions.SandboxOptions _options;
    private readonly ILogger<DockerSandbox> _logger;
    private readonly Lazy<DockerClient> _client;

    public DockerSandbox(IOptions<AgentsOptions> options, ILogger<DockerSandbox> logger)
    {
        _options = options.Value.Sandbox;
        _logger = logger;
        _client = new Lazy<DockerClient>(() => CreateClient(_options.DockerEndpoint));
    }

    public string Name => SandboxName;

    /// <summary>
    /// A client for the configured endpoint, else <c>DOCKER_HOST</c>, else the platform default. On Windows
    /// Docker Desktop may expose only its <c>dockerDesktopLinuxEngine</c> pipe, so that one is used when the
    /// classic <c>docker_engine</c> pipe is absent.
    /// </summary>
    public static DockerClient CreateClient(string? endpoint = null)
    {
        var resolved = ResolveEndpoint(endpoint);
        var configuration = resolved is null ? new DockerClientConfiguration() : new DockerClientConfiguration(new Uri(resolved));
        return configuration.CreateClient();
    }

    public static string? ResolveEndpoint(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var fromEnvironment = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;
        if (OperatingSystem.IsWindows() && !File.Exists(@"\\.\pipe\docker_engine") && File.Exists(@"\\.\pipe\dockerDesktopLinuxEngine"))
            return "npipe://./pipe/dockerDesktopLinuxEngine";
        return null;
    }

    /// <summary>The Docker client (created on first use).</summary>
    public DockerClient Client => _client.Value;

    public async Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        if (request.Command.Count == 0)
            return SandboxResult.Failed(Name, "No command given.", watch.Elapsed);

        var timeout = request.Timeout ?? TimeSpan.FromSeconds(_options.TimeoutSeconds);
        var memory = request.MemoryBytes ?? _options.MemoryBytes;
        var cpus = request.Cpus ?? _options.Cpus;
        var maxOutput = request.MaxOutputBytes ?? _options.MaxOutputBytes;

        var hostDir = Path.Combine(Path.GetTempPath(), "intelligently-sandbox", Guid.NewGuid().ToString("N"));
        string? containerId = null;
        try
        {
            Directory.CreateDirectory(hostDir);
            SandboxFiles.Write(hostDir, request.Files);

            var client = Client;
            await EnsureImageAsync(client, ct);

            var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = _options.Image,
                Cmd = request.Command.ToList(),
                WorkingDir = _options.WorkingDirectory,
                NetworkDisabled = true,
                Env = request.Environment?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? [],
                HostConfig = new HostConfig
                {
                    Binds = [$"{hostDir}:{_options.WorkingDirectory}"],
                    NetworkMode = "none",
                    Memory = memory,
                    MemorySwap = memory,
                    NanoCPUs = (long)(cpus * 1_000_000_000),
                    PidsLimit = _options.PidsLimit,
                    CapDrop = ["ALL"],
                    SecurityOpt = ["no-new-privileges"],
                },
            }, ct);
            containerId = created.ID;

            await client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);

            var timedOut = false;
            long? statusCode = null;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(timeout);
                try
                {
                    var wait = await client.Containers.WaitContainerAsync(containerId, deadline.Token);
                    statusCode = wait.StatusCode;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    timedOut = true;
                    _logger.LogInformation("Sandbox container {Container} exceeded {Timeout}; killing it", containerId, timeout);
                    await TryKillAsync(client, containerId);
                }
            }

            var inspect = await client.Containers.InspectContainerAsync(containerId, CancellationToken.None);
            var oomKilled = inspect.State?.OOMKilled ?? false;
            var exitCode = statusCode ?? inspect.State?.ExitCode;

            var (stdout, stderr, truncated) = await ReadLogsAsync(client, containerId, maxOutput);
            return new SandboxResult(
                Name,
                timedOut ? null : (int?)exitCode,
                stdout,
                stderr,
                timedOut,
                oomKilled,
                watch.Elapsed,
                truncated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker sandbox run failed");
            return SandboxResult.Failed(Name, $"{ex.GetType().Name}: {ex.Message}", watch.Elapsed);
        }
        finally
        {
            if (containerId is not null)
                await TryRemoveAsync(containerId);
            SandboxFiles.TryDelete(hostDir);
        }
    }

    private async Task EnsureImageAsync(DockerClient client, CancellationToken ct)
    {
        try
        {
            await client.Images.InspectImageAsync(_options.Image, ct);
            return;
        }
        catch (DockerImageNotFoundException) when (_options.PullImage)
        {
            _logger.LogInformation("Pulling sandbox image {Image}", _options.Image);
        }

        var (name, tag) = SplitImage(_options.Image);
        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = name, Tag = tag },
            new AuthConfig(),
            new Progress<JSONMessage>(),
            ct);
    }

    private static (string Name, string Tag) SplitImage(string image)
    {
        var slash = image.LastIndexOf('/');
        var colon = image.LastIndexOf(':');
        return colon > slash ? (image[..colon], image[(colon + 1)..]) : (image, "latest");
    }

    private async Task TryKillAsync(DockerClient client, string containerId)
    {
        try
        {
            await client.Containers.KillContainerAsync(containerId, new ContainerKillParameters { Signal = "SIGKILL" }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Killing sandbox container {Container} failed (it may have exited)", containerId);
        }
    }

    private async Task TryRemoveAsync(string containerId)
    {
        try
        {
            await Client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Removing sandbox container {Container} failed", containerId);
        }
    }

    private static async Task<(string Stdout, string Stderr, bool Truncated)> ReadLogsAsync(DockerClient client, string containerId, int maxOutput)
    {
        using var logs = await client.Containers.GetContainerLogsAsync(
            containerId,
            tty: false,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true },
            CancellationToken.None);

        var stdout = new BoundedBuffer(maxOutput);
        var stderr = new BoundedBuffer(maxOutput);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await logs.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);
            if (read.EOF) break;
            if (read.Count == 0) continue;
            var target = read.Target == MultiplexedStream.TargetStream.StandardError ? stderr : stdout;
            target.Append(buffer, read.Count);
        }

        return (stdout.ToString(), stderr.ToString(), stdout.Truncated || stderr.Truncated);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated) _client.Value.Dispose();
    }

    /// <summary>Accumulates at most <c>capacity</c> bytes and remembers whether more arrived.</summary>
    internal sealed class BoundedBuffer
    {
        private readonly MemoryStream _stream = new();
        private readonly int _capacity;

        public BoundedBuffer(int capacity)
        {
            _capacity = Math.Max(0, capacity);
        }

        public bool Truncated { get; private set; }

        public void Append(byte[] bytes, int count)
        {
            var room = _capacity - (int)_stream.Length;
            if (count > room)
            {
                Truncated = true;
                count = Math.Max(0, room);
            }
            if (count > 0) _stream.Write(bytes, 0, count);
        }

        public override string ToString()
        {
            var text = Encoding.UTF8.GetString(_stream.GetBuffer(), 0, (int)_stream.Length);
            return Truncated ? text + "\n…[output truncated]" : text;
        }
    }
}

/// <summary>Writes submitted files under a root directory, refusing paths that would escape it.</summary>
internal static class SandboxFiles
{
    public static void Write(string root, IReadOnlyList<SandboxFile> files)
    {
        var fullRoot = Path.GetFullPath(root);
        foreach (var file in files)
        {
            var target = Resolve(fullRoot, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (file.Bytes is not null)
                File.WriteAllBytes(target, file.Bytes);
            else
                File.WriteAllText(target, file.Content ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    public static string Resolve(string fullRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException($"Sandbox file path '{relativePath}' must be relative.");
        var target = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!target.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Sandbox file path '{relativePath}' escapes the working directory.");
        return target;
    }

    public static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best effort: the sandbox folder lives under the temp directory.
        }
    }
}
