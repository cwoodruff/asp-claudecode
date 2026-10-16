using System.Diagnostics.Metrics;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Telemetry;
using Intelligently.Infrastructure.Agents.Context;
using Microsoft.Extensions.Logging;

namespace Intelligently.UnitTests.Agents;

/// <summary>A repeated, previously cached prefix that reads zero cache tokens raises the miss counter and a warning.</summary>
public class CacheHealthMonitorTests
{
    private sealed class RecordingLogger : ILogger<CacheHealthMonitor>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Metrics : IDisposable
    {
        private readonly MeterListener _listener = new();
        public long Misses;
        public long Reads;
        public long Creations;

        public Metrics()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AgentTelemetry.MeterName) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                switch (instrument.Name)
                {
                    case "agent.cache.repeated_prefix_misses": Interlocked.Add(ref Misses, value); break;
                    case "agent.cache.read_input_tokens": Interlocked.Add(ref Reads, value); break;
                    case "agent.cache.creation_input_tokens": Interlocked.Add(ref Creations, value); break;
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    private static ModelUsage Usage(int read, int creation) => new("claude-opus-5-5", 5000, 100, read, creation);

    [Fact]
    public void Zero_cache_reads_on_a_repeated_cached_prefix_counts_a_miss_and_warns()
    {
        using var metrics = new Metrics();
        var logger = new RecordingLogger();
        var monitor = new CacheHealthMonitor(logger);
        var run = Guid.NewGuid();

        var first = monitor.Observe(run, "prefix-A", Usage(read: 0, creation: 4000), AgentStage.Plan);
        var second = monitor.Observe(run, "prefix-A", Usage(read: 0, creation: 4000), AgentStage.Act);
        var third = monitor.Observe(run, "prefix-A", Usage(read: 4000, creation: 0), AgentStage.Act);

        first.Should().Be(new CacheObservation(RepeatedPrefix: false, Miss: false), "the first call writes the cache");
        second.Should().Be(new CacheObservation(RepeatedPrefix: true, Miss: true));
        third.Should().Be(new CacheObservation(RepeatedPrefix: true, Miss: false));

        metrics.Misses.Should().Be(1);
        metrics.Reads.Should().Be(4000);
        metrics.Creations.Should().Be(8000);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which.Message.Should().Contain(run.ToString()).And.Contain("zero cache tokens");
    }

    [Fact]
    public void A_prefix_that_was_never_cached_or_that_changed_does_not_count_as_a_miss()
    {
        using var metrics = new Metrics();
        var logger = new RecordingLogger();
        var monitor = new CacheHealthMonitor(logger);
        var run = Guid.NewGuid();

        // Too short to cache: no creation on the first call, so a zero read on the repeat is expected.
        monitor.Observe(run, "short", Usage(read: 0, creation: 0));
        monitor.Observe(run, "short", Usage(read: 0, creation: 0)).Miss.Should().BeFalse();

        // Cached, then the prefix changes: a zero read is a rebuild, not a miss.
        monitor.Observe(run, "prefix-A", Usage(read: 0, creation: 4000));
        monitor.Observe(run, "prefix-B", Usage(read: 0, creation: 4000)).Should().Be(new CacheObservation(false, false));

        // Another run repeating the same prefix is tracked separately.
        monitor.Observe(Guid.NewGuid(), "prefix-B", Usage(read: 0, creation: 0)).RepeatedPrefix.Should().BeFalse();
        monitor.Observe(null, "prefix-B", Usage(read: 0, creation: 0)).Should().Be(new CacheObservation(false, false));

        metrics.Misses.Should().Be(0);
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }
}
