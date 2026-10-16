using System.Text.Json;
using Intelligently.Core.Agents.Tools;

namespace Intelligently.IntegrationTests.Agents;

/// <summary>A Destructive native tool: every call needs an instructor's approval before it runs.</summary>
public sealed class StubDestructiveTool : IToolHandler
{
    public const string ToolName = "delete_notes";

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { course_id = new { type = "integer" }, note_id = new { type = "integer" } },
        required = new[] { "course_id", "note_id" },
        additionalProperties = false,
    });

    private int _executions;

    public ToolDescriptor Descriptor { get; } = new(ToolName, "Deletes a learner note.", Schema, ToolRiskLevel.Destructive, "course_id");

    public int Executions => _executions;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _executions);
        return Task.FromResult(ToolResult.Ok(call, "{\"deleted\":true}"));
    }
}
