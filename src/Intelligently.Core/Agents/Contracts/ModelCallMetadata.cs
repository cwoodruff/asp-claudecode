namespace Intelligently.Core.Agents.Contracts;

/// <summary>
/// What the Evidence Plane records about one model call beyond its usage: the provider's reasoning
/// summary (thinking returned with <c>display: summarized</c>, stored as text), the stop reason, the
/// effort the call was made at, and the wall-clock latency of the request.
/// </summary>
public sealed record ModelCallMetadata(
    string? ReasoningSummary,
    string? StopReason,
    string? Effort,
    double LatencyMs)
{
    public static readonly ModelCallMetadata None = new(null, null, null, 0);
}
