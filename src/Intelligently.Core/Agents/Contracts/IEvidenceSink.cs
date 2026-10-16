using System.Diagnostics;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>
/// Evidence Plane (plan section 1.5): makes every run reconstructible. One root span per run, one child
/// span per stage, one grandchild per model call and per tool call, append-only <see cref="AgentTrace"/>
/// rows, and the run's totals as an analytics event when it ends.
/// </summary>
public interface IEvidenceSink
{
    /// <summary>
    /// Starts the run's root span and records its trace and span ids on the run so every later stage, in
    /// any process, is a child of it. Returns the activity (already stopped: the run outlives any process).
    /// </summary>
    Activity? StartRun(AgentRun run);

    /// <summary>Starts a span for one stage execution as a child of the run's root span; dispose it when the stage ends.</summary>
    Activity? StartStage(AgentRun run, AgentStage stage);

    /// <summary>Starts a span for one model call under the current stage span; dispose it when the call returns.</summary>
    Activity? StartModelCall(AgentRun run, AgentStage stage, string model, string? effort);

    /// <summary>Starts a span for one tool call under the current stage span; dispose it when the tool returns.</summary>
    Activity? StartToolCall(AgentRun run, ToolCall call, ToolDescriptor? descriptor);

    /// <summary>Appends a trace row for the run. Committed by the caller with the stage.</summary>
    Task RecordAsync(AgentRun run, AgentTrace trace, CancellationToken ct = default);

    /// <summary>
    /// Called once when the run reaches a terminal stage: records the totals (tokens, cost, outcome) as an
    /// <c>AIAnalyticsEvent</c> for existing reporting. Committed by the caller with the stage.
    /// </summary>
    Task FinishRunAsync(AgentRun run, CancellationToken ct = default);
}
