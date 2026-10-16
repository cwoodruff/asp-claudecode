using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Intelligently.Core.Telemetry;

/// <summary>
/// Shared OpenTelemetry sources for the autonomous agent runtime. Register
/// <see cref="ActivitySourceName"/> with the tracer provider and <see cref="MeterName"/>
/// with the meter provider so spans and metrics are exported.
/// </summary>
public static class AgentTelemetry
{
    public const string ActivitySourceName = "Intelligently.Agents";
    public const string MeterName = "Intelligently.Agents";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    /// <summary>Prompt-cache tokens read on model calls.</summary>
    public static readonly Counter<long> CacheReadInputTokens =
        Meter.CreateCounter<long>("agent.cache.read_input_tokens", "{token}", "Input tokens served from the prompt cache.");

    /// <summary>Prompt-cache tokens written on model calls.</summary>
    public static readonly Counter<long> CacheCreationInputTokens =
        Meter.CreateCounter<long>("agent.cache.creation_input_tokens", "{token}", "Input tokens written to the prompt cache.");

    /// <summary>Model calls that repeated a cached prefix within a run and still read zero cache tokens.</summary>
    public static readonly Counter<long> CacheRepeatedPrefixMisses =
        Meter.CreateCounter<long>("agent.cache.repeated_prefix_misses", "{call}", "Calls whose repeated prefix was not served from the cache.");

    /// <summary>Tokens of every class charged to runs, tagged by model, stage and token class (input, output, cache_read, cache_creation).</summary>
    public static readonly Counter<long> Tokens =
        Meter.CreateCounter<long>("agent.tokens", "{token}", "Tokens charged to agent runs by model, stage and token class.");

    /// <summary>USD charged to runs, tagged by model and stage.</summary>
    public static readonly Counter<double> CostUsd =
        Meter.CreateCounter<double>("agent.cost_usd", "USD", "Model cost charged to agent runs by model and stage.");

    /// <summary>Wall-clock duration of one stage execution, tagged by stage and the stage it led to.</summary>
    public static readonly Histogram<double> StageDuration =
        Meter.CreateHistogram<double>("agent.stage.duration", "ms", "Duration of one stage execution.");

    public const string TagModel = "gen_ai.request.model";
    public const string TagStage = "agent.stage";
    public const string TagTokenClass = "agent.token_class";
    public const string TagNextStage = "agent.next_stage";
}
