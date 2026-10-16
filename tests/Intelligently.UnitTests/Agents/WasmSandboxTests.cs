using FluentAssertions;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Infrastructure.Agents.Execution.Sandboxes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Intelligently.UnitTests.Agents;

/// <summary>The real <see cref="WasmSandbox"/> on tiny WAT modules: output capture, fuel, epoch timeout, memory cap.</summary>
public class WasmSandboxTests
{
    private const string HelloModule = """
        (module
          (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
          (memory (export "memory") 1)
          (data (i32.const 8) "hello from wasm\n")
          (func (export "_start")
            (i32.store (i32.const 0) (i32.const 8))
            (i32.store (i32.const 4) (i32.const 16))
            (drop (call $fd_write (i32.const 1) (i32.const 0) (i32.const 1) (i32.const 24)))
            (i32.store (i32.const 0) (i32.const 8))
            (i32.store (i32.const 4) (i32.const 5))
            (drop (call $fd_write (i32.const 2) (i32.const 0) (i32.const 1) (i32.const 24)))))
        """;

    private const string SpinModule = """
        (module
          (memory 1)
          (func (export "_start")
            (loop $forever (br $forever))))
        """;

    private const string ExitModule = """
        (module
          (import "wasi_snapshot_preview1" "proc_exit" (func $proc_exit (param i32)))
          (memory (export "memory") 1)
          (func (export "_start") (call $proc_exit (i32.const 7))))
        """;

    /// <summary>Grows memory to 64 pages (4 MB) and touches the last page; under a 2-page cap the grow fails and the store traps.</summary>
    private const string GrowModule = """
        (module
          (memory 1)
          (func (export "_start")
            (drop (memory.grow (i32.const 63)))
            (i32.store (i32.const 4000000) (i32.const 1))))
        """;

    private static WasmSandbox Sandbox(long fuel = 2_000_000_000, int timeoutSeconds = 10, long memoryBytes = 64L * 1024 * 1024) => new(
        Options.Create(new AgentsOptions
        {
            Sandbox = new AgentsOptions.SandboxOptions
            {
                Wasm = new AgentsOptions.WasmOptions { Fuel = fuel, TimeoutSeconds = timeoutSeconds, MemoryBytes = memoryBytes },
            },
        }),
        NullLogger<WasmSandbox>.Instance);

    private static SandboxRequest Request(string wat, params string[] args) =>
        new(["main.wat", .. args], [new SandboxFile("main.wat", wat)]);

    [Fact]
    public async Task Runs_a_wasi_module_and_captures_stdout_and_stderr()
    {
        var result = await Sandbox().RunAsync(Request(HelloModule, "arg1"));

        result.Error.Should().BeNull();
        result.Sandbox.Should().Be("wasm");
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Be("hello from wasm\n");
        result.Stderr.Should().Be("hello");
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Exit_status_of_proc_exit_is_the_exit_code()
    {
        var result = await Sandbox().RunAsync(Request(ExitModule));

        result.Error.Should().BeNull();
        result.ExitCode.Should().Be(7);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Fuel_exhaustion_stops_an_endless_loop_and_is_reported_as_a_timeout()
    {
        var result = await Sandbox(fuel: 100_000).RunAsync(Request(SpinModule));

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().BeNull();
        result.Error.Should().Contain("fuel");
    }

    [Fact]
    public async Task Epoch_deadline_stops_an_endless_loop_within_the_wall_clock_limit()
    {
        var started = DateTime.UtcNow;

        var result = await Sandbox().RunAsync(Request(SpinModule) with { Timeout = TimeSpan.FromMilliseconds(300) });

        result.TimedOut.Should().BeTrue();
        result.Error.Should().Contain("limit");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Memory_cap_stops_a_module_that_grows_past_it()
    {
        var result = await Sandbox().RunAsync(Request(GrowModule) with { MemoryBytes = 2 * 65536 });

        result.MemoryExceeded.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Missing_module_and_invalid_module_are_errors_not_exceptions()
    {
        var missing = await Sandbox().RunAsync(new SandboxRequest(["nothing.wasm"], []));
        missing.Error.Should().Contain("not among the submitted files");

        var invalid = await Sandbox().RunAsync(Request("(module (func (export \"_start\") (i32.add)))"));
        invalid.Error.Should().Contain("compiled");
    }
}
