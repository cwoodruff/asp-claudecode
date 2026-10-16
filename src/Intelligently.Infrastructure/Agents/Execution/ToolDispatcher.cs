using System.Text.Json;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Execution;

/// <summary>
/// Single entry point for side effects. Asks the Control Plane about every call
/// before executing any, executes the allowed ones, and records each outcome as evidence.
/// </summary>
public sealed class ToolDispatcher : IToolDispatcher
{
    private readonly IToolRegistry _registry;
    private readonly IPolicyDecisionPoint _pdp;
    private readonly IEvidenceSink _evidence;
    private readonly AgentsOptions _options;
    private readonly ILogger<ToolDispatcher> _logger;

    public ToolDispatcher(
        IToolRegistry registry,
        IPolicyDecisionPoint pdp,
        IEvidenceSink evidence,
        IOptions<AgentsOptions> options,
        ILogger<ToolDispatcher> logger)
    {
        _registry = registry;
        _pdp = pdp;
        _evidence = evidence;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DispatchResult> DispatchAsync(AgentRun run, IReadOnlyList<ToolCall> calls, CancellationToken ct = default)
    {
        var principal = new RunPrincipal(run.UserId, run.CourseId, []);
        var decisions = new List<(ToolCall Call, IToolHandler? Handler, PolicyDecision Decision)>(calls.Count);

        foreach (var call in calls)
        {
            if (!_registry.TryGetHandler(call.Name, out var handler))
            {
                decisions.Add((call, null, PolicyDecision.Deny($"Unknown tool '{call.Name}'.")));
                continue;
            }

            var decision = await _pdp.EvaluateAsync(call, handler.Descriptor, principal, ct);
            if (decision.Verdict == PolicyVerdict.RequireApproval)
            {
                _logger.LogInformation("Run {RunId}: tool {Tool} requires approval ({Reason})", run.Id, call.Name, decision.Reason);
                return new DispatchResult([], RequiresApproval: true, decision.Reason);
            }

            decisions.Add((call, handler, decision));
        }

        // Phase 1 executes sequentially: the single native tool shares the scoped DbContext.
        var results = new List<ToolResult>(calls.Count);
        foreach (var (call, handler, decision) in decisions)
        {
            var result = decision.Verdict == PolicyVerdict.Allow && handler is not null
                ? await ExecuteAsync(handler, call, ct)
                : ToolResult.Error(call, $"Denied by policy: {decision.Reason}");

            result = Truncate(result);
            results.Add(result);

            await _evidence.RecordAsync(run, new AgentTrace
            {
                Stage = run.Stage,
                Kind = AgentTrace.KindToolCall,
                Payload = JsonSerializer.Serialize(new
                {
                    call = new { call.Id, call.Name, input = call.Input },
                    decision = decision.Verdict.ToString(),
                    decisionReason = decision.Reason,
                    riskLevel = handler?.Descriptor.RiskLevel.ToString(),
                    result = new { result.IsError, result.Content },
                }),
            }, ct);
        }

        return new DispatchResult(results, RequiresApproval: false);
    }

    private async Task<ToolResult> ExecuteAsync(IToolHandler handler, ToolCall call, CancellationToken ct)
    {
        try
        {
            return await handler.ExecuteAsync(call, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Tool {Tool} failed", call.Name);
            return ToolResult.Error(call, $"Tool '{call.Name}' threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private ToolResult Truncate(ToolResult result)
    {
        var max = _options.MaxToolResultChars;
        if (max <= 0 || result.Content.Length <= max) return result;
        return result with { Content = result.Content[..max] + $"\n…[truncated {result.Content.Length - max} chars]" };
    }
}
