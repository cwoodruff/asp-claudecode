using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intelligently.IntegrationTests.Evals;

/// <summary>
/// A stored trace: the operational view of one run (stages visited, steps, evidence rows) as the Admin trace
/// viewer would show it, checked in as JSON under <c>Evals/Fixtures</c>. The eval harness replays fixtures
/// through the rubric so prompt or policy changes are measured against known-good runs rather than guessed.
/// </summary>
public sealed record TraceFixture
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required FixtureRun Run { get; init; }
    public List<string> Stages { get; init; } = [];
    public List<FixtureStep> Steps { get; init; } = [];
    public List<FixtureTrace> Traces { get; init; } = [];
    public FixtureEpisode? Episode { get; init; }

    /// <summary>Rubric expectations the fixture author asserts for this run.</summary>
    public required FixtureExpectations Expect { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TraceFixture Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<TraceFixture>(stream, Json)
               ?? throw new InvalidDataException($"Fixture {path} is empty.");
    }

    public static IEnumerable<string> FixturePaths(string directory) =>
        Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.Ordinal);
}

public sealed record FixtureRun
{
    public required string TaskType { get; init; }
    public required string Status { get; init; }
    public required string Stage { get; init; }
    public string? StatusReason { get; init; }
    public int PlanRevision { get; init; }
    public int TotalInputTokens { get; init; }
    public int TotalOutputTokens { get; init; }
    public decimal TotalCostUsd { get; init; }
    public FixtureBudget Budget { get; init; } = new();
}

public sealed record FixtureBudget
{
    public int ReplansLeft { get; init; } = 3;
    public int RetriesLeft { get; init; } = 2;
    public int ToolCallsLeft { get; init; } = 40;
    public int TokensLeft { get; init; } = 300_000;
}

public sealed record FixtureStep
{
    public int PlanRevision { get; init; }
    public int PlanStepIndex { get; init; }
    public int Attempt { get; init; } = 1;
    public required string Status { get; init; }
    public string? VerifierMessage { get; init; }
    public List<string> ToolsCalled { get; init; } = [];
    public bool HasErrorResult { get; init; }
}

public sealed record FixtureTrace
{
    public required string Stage { get; init; }
    public required string Kind { get; init; }
    public string? Model { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CacheReadInputTokens { get; init; }
    public int CacheCreationInputTokens { get; init; }
    public decimal CostUsd { get; init; }
    public string? ReasoningSummary { get; init; }
    public string? StopReason { get; init; }
    public string? Effort { get; init; }
    public double? LatencyMs { get; init; }

    /// <summary>For ToolCall rows: the PDP verdict; for Transition rows: the stage moved to; for SandboxLog rows: the exit code.</summary>
    public string? Decision { get; init; }
    public string? To { get; init; }
    public int? ExitCode { get; init; }
}

public sealed record FixtureEpisode
{
    public required string Outcome { get; init; }
    public bool HasSummary { get; init; } = true;
    public int? KnowledgeEntryId { get; init; }
}

public sealed record FixtureExpectations
{
    /// <summary>The outcome class of the run: <c>completed</c>, <c>recovered</c> or <c>escalated</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Hard ceiling on the run's cost in USD; a fixture that exceeds it fails the cost criterion.</summary>
    public decimal MaxCostUsd { get; init; } = 1m;

    /// <summary>Hard ceiling on the number of model calls.</summary>
    public int MaxModelCalls { get; init; } = 10;

    /// <summary>Tools every passed step must have called at least once, across the run.</summary>
    public List<string> RequiredTools { get; init; } = [];
}
