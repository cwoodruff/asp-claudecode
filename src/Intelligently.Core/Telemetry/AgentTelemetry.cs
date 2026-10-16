using System.Diagnostics;

namespace Intelligently.Core.Telemetry;

/// <summary>
/// Shared OpenTelemetry source for the autonomous agent runtime. Register
/// <see cref="ActivitySourceName"/> with the tracer provider so spans are exported.
/// </summary>
public static class AgentTelemetry
{
    public const string ActivitySourceName = "Intelligently.Agents";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
