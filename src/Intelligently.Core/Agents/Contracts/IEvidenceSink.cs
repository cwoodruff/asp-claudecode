using System.Diagnostics;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>
/// Evidence Plane: makes every run reconstructible. Writes append-only
/// <see cref="AgentTrace"/> rows and OpenTelemetry spans.
/// </summary>
public interface IEvidenceSink
{
    /// <summary>Starts a span for one stage execution; dispose it when the stage ends.</summary>
    Activity? StartStage(AgentRun run, AgentStage stage);

    /// <summary>Appends a trace row for the run. Committed by the caller with the stage.</summary>
    Task RecordAsync(AgentRun run, AgentTrace trace, CancellationToken ct = default);
}
