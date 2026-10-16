using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Tools;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Execution;

/// <summary>
/// ReadOnly native tool: runs submitted lab code in the throwaway sandbox (plan section 1.3). ReadOnly
/// because the sandbox is discarded afterwards; nothing outside it changes. The command comes from the
/// per-language template in <c>Agents:Sandbox:Languages</c>, and an expected output, when given, is
/// compared against stdout so Verify has a ground-truth gate without a model judgement.
/// </summary>
public sealed class RunLabCodeTool : NativeTool<RunLabCodeInput, RunLabCodeOutput>
{
    public const string ToolName = "run_lab_code";
    private const string EntrypointPlaceholder = "{entrypoint}";

    private readonly ISandbox _sandbox;
    private readonly AgentsOptions.SandboxOptions _options;

    public RunLabCodeTool(ISandbox sandbox, IOptions<AgentsOptions> options)
        : base(
            ToolName,
            "Runs the learner's lab code in an isolated sandbox with no network and returns stdout, stderr, the exit code and whether it timed out. " +
            "Call it to reproduce a build or runtime error, or to check a proposed fix. Pass every file the program needs.",
            ToolRiskLevel.ReadOnly,
            courseScopeArgument: null)
    {
        _sandbox = sandbox;
        _options = options.Value.Sandbox;
    }

    protected override async Task<RunLabCodeOutput> ExecuteAsync(RunLabCodeInput input, ToolContext context, CancellationToken ct)
    {
        if (!_options.Languages.TryGetValue(input.Language ?? string.Empty, out var template) || template.Length == 0)
        {
            return Failure($"Unsupported language '{input.Language}'. Supported: {string.Join(", ", _options.Languages.Keys.OrderBy(k => k, StringComparer.Ordinal))}.");
        }

        if (input.Files is null || input.Files.Count == 0)
            return Failure("No files were submitted.");
        if (string.IsNullOrWhiteSpace(input.Entrypoint) || !input.Files.Any(f => string.Equals(f.Path, input.Entrypoint, StringComparison.Ordinal)))
            return Failure($"Entrypoint '{input.Entrypoint}' is not among the submitted files.");

        var command = template.Select(part => part.Replace(EntrypointPlaceholder, input.Entrypoint, StringComparison.Ordinal)).ToList();
        var files = input.Files.Select(f => new SandboxFile(f.Path, f.Content)).ToList();

        var result = await _sandbox.RunAsync(new SandboxRequest(command, files), ct);

        bool? matches = input.ExpectedOutput is null
            ? null
            : result.Succeeded && string.Equals(result.Stdout.TrimEnd(), input.ExpectedOutput.TrimEnd(), StringComparison.Ordinal);

        return new RunLabCodeOutput(
            result.Sandbox,
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            result.TimedOut,
            result.MemoryExceeded,
            result.OutputTruncated,
            Math.Round(result.Duration.TotalSeconds, 3),
            result.Succeeded,
            matches,
            result.Error);
    }

    private RunLabCodeOutput Failure(string error) =>
        new(_sandbox.Name, null, string.Empty, string.Empty, false, false, false, 0, false, null, error);
}
