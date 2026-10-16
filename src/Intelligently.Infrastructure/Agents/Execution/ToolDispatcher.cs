using System.Text.Json;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Execution;

/// <summary>
/// Single entry point for side effects. Asks the Control Plane about every call before executing
/// any, then executes the allowed ones of the turn concurrently (a replay returns the idempotency
/// barrier's stored result without executing), validates each successful result against the tool's
/// output schema, records covered results in the barrier, and writes every outcome as evidence. All
/// results come back in one list in call order, failures included as <c>is_error</c> results.
/// </summary>
public sealed class ToolDispatcher : IToolDispatcher
{
    private readonly IToolRegistry _registry;
    private readonly IPolicyDecisionPoint _pdp;
    private readonly IIdempotencyBarrier _barrier;
    private readonly IEvidenceSink _evidence;
    private readonly ToolOutputValidator _validator;
    private readonly AgentsOptions _options;
    private readonly ILogger<ToolDispatcher> _logger;

    public ToolDispatcher(
        IToolRegistry registry,
        IPolicyDecisionPoint pdp,
        IIdempotencyBarrier barrier,
        IEvidenceSink evidence,
        ToolOutputValidator validator,
        IOptions<AgentsOptions> options,
        ILogger<ToolDispatcher> logger)
    {
        _registry = registry;
        _pdp = pdp;
        _barrier = barrier;
        _evidence = evidence;
        _validator = validator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DispatchResult> DispatchAsync(AgentRun run, IReadOnlyList<ToolCall> calls, CancellationToken ct = default)
    {
        var principal = run.Principal;
        var context = ToolContext.For(run);
        var decisions = new List<(ToolCall Call, IToolHandler? Handler, PolicyDecision Decision)>(calls.Count);

        // 1. Control Plane first, sequentially: the PDP, barrier and rate limiter share the scope.
        foreach (var call in calls)
        {
            if (!_registry.TryGetHandler(call.Name, out var handler))
            {
                decisions.Add((call, null, PolicyDecision.Deny($"Unknown tool '{call.Name}'.")));
                continue;
            }

            var decision = await _pdp.EvaluateAsync(new PolicyRequest(run, call, handler.Descriptor, principal), ct);
            if (decision.Verdict == PolicyVerdict.RequireApproval)
            {
                _logger.LogInformation("Run {RunId}: tool {Tool} requires approval ({Reason})", run.Id, call.Name, decision.Reason);
                return new DispatchResult([], RequiresApproval: true, decision.Reason, call);
            }

            decisions.Add((call, handler, decision));
        }

        // 2. Execute the allowed calls of the turn concurrently; order is restored by index below.
        var executions = decisions.Select(d => ExecuteAsync(d.Call, d.Handler, d.Decision, context, ct)).ToList();
        var outcomes = await Task.WhenAll(executions);

        // 3. Record, truncate and trace in call order (barrier and evidence share the scoped DbContext).
        var results = new List<ToolResult>(calls.Count);
        var recordedKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < decisions.Count; i++)
        {
            var (call, handler, decision) = decisions[i];
            var (result, replayed) = outcomes[i];

            if (!replayed && !result.IsError && decision.IdempotencyKey is { } key && recordedKeys.Add($"{call.Name}|{key}"))
                await _barrier.RecordAsync(run.Id, call, key, result, ct);

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
                    rule = decision.Rule,
                    ruleKind = decision.RuleKind?.ToString(),
                    riskLevel = handler?.Descriptor.RiskLevel.ToString(),
                    replayed,
                    parallel = decisions.Count > 1,
                    result = new { result.IsError, result.ErrorKind, result.Content },
                }),
            }, ct);
        }

        return new DispatchResult(results, RequiresApproval: false);
    }

    private async Task<(ToolResult Result, bool Replayed)> ExecuteAsync(ToolCall call, IToolHandler? handler, PolicyDecision decision, ToolContext context, CancellationToken ct)
    {
        if (decision.Verdict != PolicyVerdict.Allow || handler is null)
        {
            var reason = decision.Reason ?? "no reason given";
            return (decision.RuleKind == PolicyRuleKind.RateLimit ? ToolResult.RateLimited(call, reason) : ToolResult.Denied(call, reason), false);
        }

        if (decision.ReplayedResult is { } stored)
            return (stored, true);

        ToolResult result;
        try
        {
            result = await handler.ExecuteAsync(call, context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Tool {Tool} failed", call.Name);
            result = ToolResult.Error(call, $"Tool '{call.Name}' threw {ex.GetType().Name}: {ex.Message}");
        }

        // Normalize to the call that was made, whatever the handler put in the result.
        if (result.ToolCallId != call.Id || result.Name != call.Name)
            result = result with { ToolCallId = call.Id, Name = call.Name };

        return (_validator.Validate(handler.Descriptor, call, result), false);
    }

    private ToolResult Truncate(ToolResult result)
    {
        var max = _options.MaxToolResultChars;
        if (max <= 0 || result.Content.Length <= max) return result;
        return result with { Content = result.Content[..max] + $"\n…[truncated {result.Content.Length - max} chars]" };
    }
}
