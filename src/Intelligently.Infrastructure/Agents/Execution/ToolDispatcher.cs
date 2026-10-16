using System.Diagnostics;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Evidence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intelligently.Infrastructure.Agents.Execution;

/// <summary>
/// Single entry point for side effects. Asks the Control Plane about every call before executing
/// any, then executes the allowed calls of the turn: concurrent handlers together, identical covered
/// calls coalesced into one execution whose result is fanned out under each call's id, and
/// <see cref="ToolDescriptor.Exclusive"/> handlers afterwards one at a time on the stage's unit of work.
/// Every successful result is validated against the tool's output schema, covered results are recorded
/// in the idempotency barrier (committed with the stage), and every outcome is written as evidence.
/// All results come back in one list in call order, failures included as <c>is_error</c> results.
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

    private sealed record Outcome(ToolResult Result, bool Replayed, bool Coalesced = false, double? LatencyMs = null);

    public async Task<DispatchResult> DispatchAsync(AgentRun run, IReadOnlyList<ToolCall> calls, CancellationToken ct = default, Guid? stepId = null)
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

        // 2. Execute. Identical covered calls (same tool, same argument hash) run once: the first starts the
        //    execution and the others wait for it. Exclusive handlers run after the concurrent batch, alone.
        var outcomes = new Outcome[decisions.Count];
        var inFlight = new Dictionary<string, Task<Outcome>>(StringComparer.Ordinal);
        var concurrent = new List<(int Index, Task<Outcome> Task)>();
        var exclusive = new List<int>();

        for (var i = 0; i < decisions.Count; i++)
        {
            var (call, handler, decision) = decisions[i];
            if (handler is { Descriptor.Exclusive: true } && decision.Verdict == PolicyVerdict.Allow && decision.ReplayedResult is null)
                exclusive.Add(i);
            else
                concurrent.Add((i, StartAsync(call, handler, decision, context, inFlight, ct)));
        }

        await Task.WhenAll(concurrent.Select(c => c.Task));
        foreach (var (index, task) in concurrent)
            outcomes[index] = await task;

        foreach (var index in exclusive)
        {
            var (call, handler, decision) = decisions[index];
            outcomes[index] = await StartAsync(call, handler, decision, context, inFlight, ct);
        }

        // 3. Record, truncate and trace in call order (barrier and evidence share the scoped DbContext).
        //    Each call gets its own span under the stage span, carrying the tool, risk level, PDP decision
        //    and latency; a sandbox run also leaves a SandboxLog row with its exit code and output.
        var results = new List<ToolResult>(calls.Count);
        for (var i = 0; i < decisions.Count; i++)
        {
            var (call, handler, decision) = decisions[i];
            var (result, replayed, coalesced, latencyMs) = outcomes[i];

            if (!replayed && !result.IsError && decision.IdempotencyKey is { } key)
                await _barrier.RecordAsync(run.Id, call, key, result, ct);

            var untruncated = result;
            result = Truncate(result);
            results.Add(result);

            using var span = _evidence.StartToolCall(run, call, handler?.Descriptor);
            span?.SetTag("agent.pdp.decision", decision.Verdict.ToString());
            if (decision.Rule is not null) span?.SetTag("agent.pdp.rule", decision.Rule);
            span?.SetTag("agent.tool.replayed", replayed);
            span?.SetTag("agent.tool.is_error", result.IsError);
            if (latencyMs is { } latency) span?.SetStartTime(DateTime.UtcNow.AddMilliseconds(-latency));

            await _evidence.RecordAsync(run, TraceWriter.ToolCall(run.Stage, call, result, decision, handler?.Descriptor, replayed, coalesced, decisions.Count > 1, latencyMs, stepId), ct);
            if (!replayed && TraceWriter.SandboxLog(run.Stage, call, untruncated, stepId) is { } sandboxLog)
                await _evidence.RecordAsync(run, sandboxLog, ct);
        }

        return new DispatchResult(results, RequiresApproval: false);
    }

    private Task<Outcome> StartAsync(
        ToolCall call,
        IToolHandler? handler,
        PolicyDecision decision,
        ToolContext context,
        Dictionary<string, Task<Outcome>> inFlight,
        CancellationToken ct)
    {
        if (decision.Verdict != PolicyVerdict.Allow || handler is null)
        {
            var reason = decision.Reason ?? "no reason given";
            var denied = decision.RuleKind == PolicyRuleKind.RateLimit ? ToolResult.RateLimited(call, reason) : ToolResult.Denied(call, reason);
            return Task.FromResult(new Outcome(denied, Replayed: false));
        }

        if (decision.ReplayedResult is { } stored)
            return Task.FromResult(new Outcome(stored, Replayed: true));

        if (decision.IdempotencyKey is { } key)
        {
            var coalesceKey = $"{call.Name}|{key}";
            if (inFlight.TryGetValue(coalesceKey, out var first))
                return CoalesceAsync(first, call);

            var execution = ExecuteAsync(handler, call, context, ct);
            inFlight[coalesceKey] = execution;
            return execution;
        }

        return ExecuteAsync(handler, call, context, ct);
    }

    /// <summary>A later identical call takes the first execution's result under its own tool-use id.</summary>
    private static async Task<Outcome> CoalesceAsync(Task<Outcome> first, ToolCall call)
    {
        var outcome = await first;
        return new Outcome(outcome.Result with { ToolCallId = call.Id, Name = call.Name }, Replayed: true, Coalesced: true);
    }

    private async Task<Outcome> ExecuteAsync(IToolHandler handler, ToolCall call, ToolContext context, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
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
        stopwatch.Stop();

        // Normalize to the call that was made, whatever the handler put in the result.
        if (result.ToolCallId != call.Id || result.Name != call.Name)
            result = result with { ToolCallId = call.Id, Name = call.Name };

        return new Outcome(_validator.Validate(handler.Descriptor, call, result), Replayed: false, LatencyMs: stopwatch.Elapsed.TotalMilliseconds);
    }

    private ToolResult Truncate(ToolResult result)
    {
        var max = _options.MaxToolResultChars;
        if (max <= 0 || result.Content.Length <= max) return result;
        return result with { Content = result.Content[..max] + $"\n…[truncated {result.Content.Length - max} chars]" };
    }
}
