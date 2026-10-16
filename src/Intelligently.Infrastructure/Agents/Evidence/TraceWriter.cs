using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;

namespace Intelligently.Infrastructure.Agents.Evidence;

/// <summary>
/// Builds the append-only <see cref="AgentTrace"/> rows of the Evidence Plane (plan section 1.5): the exact
/// prompt envelope sent and the raw response of a model call, a tool call with its payload, and sandbox
/// logs. Rows are handed to <see cref="IEvidenceSink.RecordAsync"/> and committed with the stage; a row is
/// never edited afterwards.
/// </summary>
public static class TraceWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    /// <summary>
    /// The model-call row: the envelope as sent (system blocks, tool names, messages with sections and cache
    /// breakpoints, prefix hash, counted prompt tokens, compaction flag), the raw response, the response
    /// content blocks, the usage per token class, the cost, the reasoning summary, stop reason, effort and latency.
    /// </summary>
    public static AgentTrace ModelCall(
        AgentStage stage,
        PromptEnvelope envelope,
        ModelUsage usage,
        string rawResponse,
        string? contentJson,
        ModelCallMetadata? metadata,
        decimal costUsd,
        Guid? stepId = null)
    {
        metadata ??= ModelCallMetadata.None;
        return new AgentTrace
        {
            Stage = stage,
            StepId = stepId,
            Kind = AgentTrace.KindModelCall,
            Model = usage.Model,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CacheReadInputTokens = usage.CacheReadInputTokens,
            CacheCreationInputTokens = usage.CacheCreationInputTokens,
            CostUsd = costUsd,
            ReasoningSummary = string.IsNullOrWhiteSpace(metadata.ReasoningSummary) ? null : metadata.ReasoningSummary,
            StopReason = metadata.StopReason,
            Effort = metadata.Effort,
            LatencyMs = metadata.LatencyMs,
            Payload = JsonSerializer.Serialize(new
            {
                envelope = new
                {
                    system = envelope.System,
                    tools = envelope.Tools.Select(t => t.Name),
                    messages = envelope.Messages,
                    prefixHash = envelope.PrefixHash(),
                    promptTokens = envelope.InputTokens,
                    compaction = envelope.Compaction,
                },
                response = rawResponse,
                content = ParseOrNull(contentJson),
                reasoningSummary = metadata.ReasoningSummary,
                stopReason = metadata.StopReason,
                effort = metadata.Effort,
                latencyMs = metadata.LatencyMs,
                costUsd,
            }, Json),
        };
    }

    /// <summary>The tool-call row: the call, the Control Plane decision, the risk level, the result text and its structured payload.</summary>
    public static AgentTrace ToolCall(
        AgentStage stage,
        ToolCall call,
        ToolResult result,
        PolicyDecision decision,
        ToolDescriptor? descriptor,
        bool replayed,
        bool coalesced,
        bool parallel,
        double? latencyMs,
        Guid? stepId = null) => new()
    {
        Stage = stage,
        StepId = stepId,
        Kind = AgentTrace.KindToolCall,
        LatencyMs = latencyMs,
        Payload = JsonSerializer.Serialize(new
        {
            call = new { call.Id, call.Name, input = call.Input },
            decision = decision.Verdict.ToString(),
            decisionReason = decision.Reason,
            rule = decision.Rule,
            ruleKind = decision.RuleKind?.ToString(),
            riskLevel = descriptor?.RiskLevel.ToString(),
            replayed,
            coalesced,
            exclusive = descriptor?.Exclusive ?? false,
            parallel,
            latencyMs,
            result = new { result.IsError, result.ErrorKind, result.Content, payload = result.Payload },
        }, Json),
    };

    /// <summary>
    /// The sandbox-log row for a tool result that ran code in a sandbox (a payload with <c>sandbox</c>,
    /// <c>exit_code</c>, <c>stdout</c> and <c>stderr</c>), or null when the result is not a sandbox run.
    /// </summary>
    public static AgentTrace? SandboxLog(AgentStage stage, ToolCall call, ToolResult result, Guid? stepId = null)
    {
        if (SandboxRun.TryFrom(result) is not { } sandbox) return null;
        return new AgentTrace
        {
            Stage = stage,
            StepId = stepId,
            Kind = AgentTrace.KindSandboxLog,
            LatencyMs = sandbox.DurationSeconds is { } seconds ? seconds * 1000 : null,
            Payload = JsonSerializer.Serialize(new
            {
                call = new { call.Id, call.Name },
                sandbox = sandbox.Sandbox,
                exitCode = sandbox.ExitCode,
                stdout = sandbox.Stdout,
                stderr = sandbox.Stderr,
                timedOut = sandbox.TimedOut,
                memoryExceeded = sandbox.MemoryExceeded,
                outputTruncated = sandbox.OutputTruncated,
                durationSeconds = sandbox.DurationSeconds,
                error = sandbox.Error,
            }, Json),
        };
    }

    private static JsonElement? ParseOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The sandbox fields of a tool result payload, recognised by shape so MCP sandboxes are covered as well as <c>run_lab_code</c>.</summary>
public sealed record SandboxRun(string Sandbox, int? ExitCode, string Stdout, string Stderr, bool TimedOut, bool MemoryExceeded, bool OutputTruncated, double? DurationSeconds, string? Error)
{
    public static SandboxRun? TryFrom(ToolResult result)
    {
        if (result.Payload is not { ValueKind: JsonValueKind.Object } payload) return null;
        if (!payload.TryGetProperty("sandbox", out var sandbox) || sandbox.ValueKind != JsonValueKind.String) return null;
        if (!payload.TryGetProperty("exit_code", out var exitCode)) return null;
        if (!payload.TryGetProperty("stdout", out var stdout) || !payload.TryGetProperty("stderr", out var stderr)) return null;

        return new SandboxRun(
            sandbox.GetString() ?? string.Empty,
            exitCode.ValueKind == JsonValueKind.Number && exitCode.TryGetInt32(out var code) ? code : null,
            stdout.ValueKind == JsonValueKind.String ? stdout.GetString() ?? string.Empty : string.Empty,
            stderr.ValueKind == JsonValueKind.String ? stderr.GetString() ?? string.Empty : string.Empty,
            Flag(payload, "timed_out"),
            Flag(payload, "memory_exceeded"),
            Flag(payload, "output_truncated"),
            payload.TryGetProperty("duration_seconds", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetDouble() : null,
            payload.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null);
    }

    private static bool Flag(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
