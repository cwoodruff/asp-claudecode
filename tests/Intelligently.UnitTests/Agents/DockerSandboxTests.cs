using FluentAssertions;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Infrastructure.Agents.Execution.Sandboxes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// The real <see cref="DockerSandbox"/> against the local engine, skipped when none answers. A small
/// image stands in for the .NET SDK image so the tests pull a few megabytes at most.
/// </summary>
public class DockerSandboxTests
{
    private const string Image = "alpine:3.21";

    private static DockerSandbox Sandbox(int maxOutputBytes = 64 * 1024) => new(
        Options.Create(new AgentsOptions
        {
            Sandbox = new AgentsOptions.SandboxOptions
            {
                Image = Image,
                TimeoutSeconds = 30,
                MemoryBytes = 64L * 1024 * 1024,
                Cpus = 0.5,
                MaxOutputBytes = maxOutputBytes,
            },
        }),
        NullLogger<DockerSandbox>.Instance);

    [DockerFact]
    public async Task Runs_the_command_on_the_mounted_files_and_captures_stdout_stderr_and_exit_code()
    {
        using var sandbox = Sandbox();
        var request = new SandboxRequest(
            ["sh", "run.sh"],
            [
                new SandboxFile("run.sh", "cat input/data.txt; echo oops >&2; ls / | grep -c work; exit 3"),
                new SandboxFile("input/data.txt", "hello from the host"),
            ])
        {
            Environment = new Dictionary<string, string> { ["LAB"] = "1" },
        };

        var result = await sandbox.RunAsync(request);

        result.Error.Should().BeNull();
        result.Sandbox.Should().Be("docker");
        result.ExitCode.Should().Be(3);
        result.Stdout.Should().Contain("hello from the host").And.Contain("1");
        result.Stderr.Should().Contain("oops");
        result.TimedOut.Should().BeFalse();
        result.MemoryExceeded.Should().BeFalse();
        result.OutputTruncated.Should().BeFalse();
    }

    [DockerFact]
    public async Task Has_no_network_and_runs_without_capabilities()
    {
        using var sandbox = Sandbox();
        var result = await sandbox.RunAsync(new SandboxRequest(
            ["sh", "-c", "ip -o link | grep -v -c LOOPBACK; cat /proc/self/status | grep CapEff"],
            []));

        result.Error.Should().BeNull();
        result.Stdout.Should().Contain("0\n", "only the loopback interface exists");
        result.Stdout.Should().Contain("CapEff:\t0000000000000000", "every capability is dropped");
    }

    [DockerFact]
    public async Task Timeout_kills_the_container_and_reports_timed_out()
    {
        using var sandbox = Sandbox();
        var started = DateTime.UtcNow;

        var result = await sandbox.RunAsync(new SandboxRequest(["sh", "-c", "echo started; sleep 60; echo done"], [])
        {
            Timeout = TimeSpan.FromSeconds(2),
        });

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().BeNull();
        result.Stdout.Should().Contain("started").And.NotContain("done");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [DockerFact]
    public async Task Output_is_capped_at_the_configured_byte_limit()
    {
        using var sandbox = Sandbox(maxOutputBytes: 1024);

        var result = await sandbox.RunAsync(new SandboxRequest(["sh", "-c", "head -c 100000 /dev/zero | tr '\\0' 'x'"], []));

        result.Error.Should().BeNull();
        result.OutputTruncated.Should().BeTrue();
        result.Stdout.Length.Should().BeLessThan(1100);
        result.Stdout.Should().EndWith("[output truncated]");
    }

    [DockerFact]
    public async Task Memory_limit_kills_a_process_that_allocates_past_it()
    {
        using var sandbox = Sandbox();

        // tail buffers everything it reads, so this grows well past the 32 MB limit.
        var result = await sandbox.RunAsync(new SandboxRequest(["sh", "-c", "head -c 200000000 /dev/zero | tail"], [])
        {
            MemoryBytes = 32L * 1024 * 1024,
            Timeout = TimeSpan.FromSeconds(60),
        });

        result.Error.Should().BeNull();
        result.Succeeded.Should().BeFalse();
        (result.MemoryExceeded || result.ExitCode == 137).Should().BeTrue($"the process should be killed (exit {result.ExitCode}, oom {result.MemoryExceeded})");
    }
}
