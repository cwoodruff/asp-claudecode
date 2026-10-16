using System.Collections.Concurrent;
using System.Diagnostics;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace Intelligently.Infrastructure.Agents.Context;

/// <summary>Outcome of one cache-health observation.</summary>
public sealed record CacheObservation(bool RepeatedPrefix, bool Miss);

/// <summary>
/// Cache health (plan section 1.2): records cache read and creation tokens as metrics and raises a
/// counter plus a warning when a run repeats a prefix that was cached on its previous call and still reads
/// zero cache tokens. A prefix counts as cached once a call on it reported cache creation or read tokens,
/// so a prefix too short to cache never warns.
/// </summary>
public sealed class CacheHealthMonitor
{
    private const int MaxTrackedRuns = 10_000;

    private readonly ConcurrentDictionary<Guid, (string PrefixHash, bool Cached)> _lastByRun = new();
    private readonly ILogger<CacheHealthMonitor> _logger;

    public CacheHealthMonitor(ILogger<CacheHealthMonitor> logger)
    {
        _logger = logger;
    }

    public CacheObservation Observe(Guid? runId, string prefixHash, ModelUsage usage, AgentStage? stage = null)
    {
        var tags = new TagList
        {
            { "gen_ai.request.model", usage.Model },
            { "agent.stage", stage?.ToString() ?? "unknown" },
        };
        AgentTelemetry.CacheReadInputTokens.Add(usage.CacheReadInputTokens, tags);
        AgentTelemetry.CacheCreationInputTokens.Add(usage.CacheCreationInputTokens, tags);

        var cachedNow = usage.CacheReadInputTokens > 0 || usage.CacheCreationInputTokens > 0;
        if (runId is not { } id)
            return new CacheObservation(false, false);

        var repeated = _lastByRun.TryGetValue(id, out var last) && last.PrefixHash == prefixHash;
        var miss = repeated && last.Cached && usage.CacheReadInputTokens == 0;
        if (miss)
        {
            AgentTelemetry.CacheRepeatedPrefixMisses.Add(1, tags);
            _logger.LogWarning(
                "Run {RunId}: repeated prompt prefix {PrefixHash} at {Stage} read zero cache tokens (input {InputTokens}, cache creation {CacheCreation}); a silent cache invalidator is likely",
                id, prefixHash[..Math.Min(12, prefixHash.Length)], stage, usage.InputTokens, usage.CacheCreationInputTokens);
        }

        if (_lastByRun.Count >= MaxTrackedRuns && !_lastByRun.ContainsKey(id))
            _lastByRun.Clear();
        _lastByRun[id] = (prefixHash, cachedNow || (repeated && last.Cached));

        return new CacheObservation(repeated, miss);
    }

    /// <summary>Drops the tracking state of a finished run.</summary>
    public void Forget(Guid runId) => _lastByRun.TryRemove(runId, out _);
}
