using Intelligently.Core.Agents.Tools;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>
/// Control Plane idempotency barrier (plan section 1.4): remembers the result of every covered call
/// keyed on (run id, tool name, SHA-256 of the canonical argument JSON) so a replayed call during
/// Recover returns the stored result instead of executing the side effect again.
/// </summary>
public interface IIdempotencyBarrier
{
    /// <summary>SHA-256 hex of the canonical (key-sorted, compact) JSON of the call's arguments.</summary>
    string ComputeKey(ToolCall call);

    /// <summary>The stored result of an identical earlier call in this run, or null when the call is new.</summary>
    Task<ToolResult?> FindAsync(Guid runId, string toolName, string key, CancellationToken ct = default);

    /// <summary>Stores the result of a freshly executed call. Committed by the caller with the stage.</summary>
    Task RecordAsync(Guid runId, ToolCall call, string key, ToolResult result, CancellationToken ct = default);
}
