using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Intelligently.UnitTests.Agents;

/// <summary>A native tool with a configurable risk level that counts its executions.</summary>
public sealed class StubTool : IToolHandler
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { course_id = new { type = new[] { "integer", "null" } }, text = new { type = "string" } },
        required = new[] { "course_id", "text" },
        additionalProperties = false,
    });

    private readonly Func<ToolCall, ToolResult>? _execute;

    public StubTool(string name, ToolRiskLevel riskLevel, string? courseScopeArgument = "course_id", Func<ToolCall, ToolResult>? execute = null)
    {
        Descriptor = new ToolDescriptor(name, $"Stub {riskLevel} tool.", Schema, riskLevel, courseScopeArgument);
        _execute = execute;
    }

    public ToolDescriptor Descriptor { get; }
    public int Executions { get; private set; }
    public List<ToolCall> Calls { get; } = [];

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct = default)
    {
        Executions++;
        Calls.Add(call);
        return Task.FromResult(_execute?.Invoke(call) ?? ToolResult.Ok(call, $"{{\"executed\":{Executions}}}"));
    }

    /// <summary>A tool context for a learner run in course 7, for calling handlers directly.</summary>
    public static ToolContext Context(string userId = "learner-1", int courseId = 7, string taskType = "LabErrorDiagnosis") =>
        new(Guid.NewGuid(), userId, courseId, taskType, RunPrincipal.Learner(userId, courseId));

    public static ToolCall Call(string tool, int? courseId, string text = "hello", string id = "toolu_stub") =>
        new(id, tool, JsonSerializer.SerializeToElement(new { course_id = courseId, text }));

    public static AgentPlan PlanFor(string tool, int steps = 1) => new()
    {
        Goal = $"Exercise {tool}.",
        Steps = Enumerable.Range(0, steps).Select(i => new PlanStep
        {
            Index = i,
            Description = $"Call {tool} (step {i}).",
            Tool = tool,
            SuccessCriteria = "The tool returns without error.",
        }).ToList(),
    };
}

/// <summary>Records every enqueue instead of buffering on a channel, so tests can assert re-enqueues.</summary>
public sealed class RecordingRunQueue : IRunQueue
{
    public List<Guid> Enqueued { get; } = [];

    public ValueTask EnqueueAsync(Guid runId, CancellationToken ct = default)
    {
        Enqueued.Add(runId);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<Guid> DequeueAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

internal static class ControlPlaneServices
{
    public static void UseTools(this IServiceCollection services, params IToolHandler[] tools)
    {
        foreach (var tool in tools)
            services.AddSingleton(tool);
    }

    public static RecordingRunQueue UseRecordingQueue(this IServiceCollection services)
    {
        var queue = new RecordingRunQueue();
        services.RemoveAll<IRunQueue>();
        services.AddSingleton<IRunQueue>(queue);
        return queue;
    }
}
