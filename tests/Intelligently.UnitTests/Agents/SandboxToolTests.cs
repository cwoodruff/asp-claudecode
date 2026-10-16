using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// <c>run_lab_code</c> over a fake <see cref="ISandbox"/>: the command comes from the language template,
/// timeouts and memory kills reach the model as a normalized, schema-valid output (never an exception),
/// and the whole run completes through the real runner.
/// </summary>
public class SandboxToolTests
{
    private static AgentTestHost Host(FakeSandbox sandbox) => new(services =>
    {
        services.RemoveAll<ISandbox>();
        services.AddSingleton<ISandbox>(sandbox);
    });

    private static async Task<RunLabCodeOutput> ExecuteAsync(AgentTestHost host, ToolCall call)
    {
        using var scope = host.Provider.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IToolRegistry>();
        registry.TryGetHandler(RunLabCodeTool.ToolName, out var handler).Should().BeTrue();
        var result = await handler.ExecuteAsync(call, StubTool.Context());
        result.IsError.Should().BeFalse(result.Content);
        result.Payload.Should().NotBeNull();
        return JsonSerializer.Deserialize<RunLabCodeOutput>(result.Content)!;
    }

    [Fact]
    public async Task Descriptor_exports_input_and_output_schemas_from_the_records()
    {
        using var host = Host(new FakeSandbox());
        using var scope = host.Provider.CreateScope();
        var descriptor = scope.ServiceProvider.GetRequiredService<IToolRegistry>().Descriptors.Single(d => d.Name == RunLabCodeTool.ToolName);

        descriptor.RiskLevel.Should().Be(ToolRiskLevel.ReadOnly, "the sandbox is throwaway");
        descriptor.Strict.Should().BeTrue();
        descriptor.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo("language", "files", "entrypoint", "expected_output");
        descriptor.InputSchema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        descriptor.InputSchema.GetProperty("properties").GetProperty("files").GetProperty("items").GetProperty("properties")
            .TryGetProperty("content", out _).Should().BeTrue();
        descriptor.InputSchema.GetProperty("properties").GetProperty("language").GetProperty("description").GetString().Should().Contain("csharp");

        descriptor.OutputSchema.Should().NotBeNull();
        descriptor.OutputSchema!.Value.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Contain(["exit_code", "stdout", "stderr", "timed_out", "memory_exceeded", "succeeded"]);
    }

    [Fact]
    public async Task Command_comes_from_the_language_template_and_files_reach_the_sandbox()
    {
        var sandbox = new FakeSandbox(r => FakeSandbox.Completed(r, 0, "hi\n"));
        using var host = Host(sandbox);

        var output = await ExecuteAsync(host, LabCode.Call(expectedOutput: "hi"));

        sandbox.Requests.Should().ContainSingle();
        sandbox.Requests[0].Command.Should().Equal("dotnet", "run", "Program.cs", "-p:PublishAot=false");
        sandbox.Requests[0].Files.Should().ContainSingle().Which.Path.Should().Be("Program.cs");
        output.Succeeded.Should().BeTrue();
        output.ExitCode.Should().Be(0);
        output.Stdout.Should().Be("hi\n");
        output.MatchesExpectedOutput.Should().BeTrue("trailing whitespace is ignored");
        output.Sandbox.Should().Be("fake");
    }

    [Fact]
    public async Task Timeout_is_reported_as_timed_out_with_the_partial_output()
    {
        using var host = Host(new FakeSandbox(FakeSandbox.TimedOut));

        var output = await ExecuteAsync(host, LabCode.Call(expectedOutput: "partial output"));

        output.TimedOut.Should().BeTrue();
        output.Succeeded.Should().BeFalse();
        output.ExitCode.Should().BeNull();
        output.Stdout.Should().Be("partial output");
        output.MatchesExpectedOutput.Should().BeFalse("a timed-out run never matches, whatever it printed");
    }

    [Fact]
    public async Task Memory_kill_is_reported_as_memory_exceeded()
    {
        using var host = Host(new FakeSandbox(FakeSandbox.MemoryKilled));

        var output = await ExecuteAsync(host, LabCode.Call());

        output.MemoryExceeded.Should().BeTrue();
        output.ExitCode.Should().Be(137);
        output.Succeeded.Should().BeFalse();
        output.Stderr.Should().Be("Killed");
    }

    [Fact]
    public async Task Unsupported_language_and_missing_entrypoint_are_errors_in_the_output_not_exceptions()
    {
        var sandbox = new FakeSandbox();
        using var host = Host(sandbox);

        var unsupported = await ExecuteAsync(host, LabCode.Call(language: "cobol"));
        unsupported.Error.Should().Contain("Unsupported language 'cobol'").And.Contain("csharp");

        var missing = await ExecuteAsync(host, LabCode.Call(entrypoint: "Program.cs") with
        {
            Input = JsonSerializer.SerializeToElement(new { language = "csharp", files = new[] { new { path = "Other.cs", content = "" } }, entrypoint = "Program.cs", expected_output = (string?)null }),
        });
        missing.Error.Should().Contain("Entrypoint 'Program.cs'");

        sandbox.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_completes_end_to_end_through_run_lab_code_and_a_timed_out_step_fails_verification()
    {
        var attempt = 0;
        var sandbox = new FakeSandbox(r => ++attempt == 1 ? FakeSandbox.TimedOut(r) : FakeSandbox.Completed(r, 0, "hi\n"));
        using var host = Host(sandbox);
        EngineStubs.PlanReturns(host.Engine, LabSpecs.SandboxPlan());
        EngineStubs.DecideReturns(host.Engine, LabCode.Call(expectedOutput: "hi"));

        var run = await host.CreateRunAsync();
        var (final, stages) = await host.RunToEndAsync(run.Id);

        // The timed-out result is not is_error (the tool ran), but the lab contract's deterministic assertions
        // (plan section 2.4, contract 2) fail the step; Recover retries it and the second attempt completes.
        final.Status.Should().Be(AgentRun.StatusCompleted);
        final.Budget.RetriesLeft.Should().Be(1);
        stages.Should().Equal(
            AgentStage.Interpret, AgentStage.Plan,
            AgentStage.Act, AgentStage.Observe, AgentStage.Verify, AgentStage.Recover,
            AgentStage.Act, AgentStage.Observe, AgentStage.Verify, AgentStage.Learn, AgentStage.Completed);
        var steps = await host.QueryAsync(db => db.AgentSteps.Where(s => s.RunId == run.Id).OrderBy(s => s.Attempt).ToListAsync());
        steps.Select(s => s.Status).Should().Equal(AgentStep.StatusFailed, AgentStep.StatusPassed);
        steps[0].VerifierMessage.Should().Contain("timed out");
        var results = JsonSerializer.Deserialize<List<ToolResult>>(steps[1].ToolResultsJson!)!;
        results.Should().ContainSingle().Which.Name.Should().Be(RunLabCodeTool.ToolName);
        var output = JsonSerializer.Deserialize<RunLabCodeOutput>(results[0].Content)!;
        output.Succeeded.Should().BeTrue();
        output.MatchesExpectedOutput.Should().BeTrue();
        sandbox.Requests.Should().HaveCount(2);
    }
}
