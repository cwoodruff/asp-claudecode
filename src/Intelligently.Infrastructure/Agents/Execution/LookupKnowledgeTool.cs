using System.Text.Json;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Infrastructure.Agents.Execution;

/// <summary>ReadOnly native tool: keyword lookup over active <c>AIKnowledgeEntry</c> rows.</summary>
public sealed class LookupKnowledgeTool : IToolHandler
{
    public const string ToolName = "lookup_knowledge";
    private const int MaxResults = 5;

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "Keywords or a short phrase to search for in the knowledge base." },
            course_id = new { type = new[] { "integer", "null" }, description = "Restrict results to this course, or null for all courses." },
        },
        required = new[] { "query", "course_id" },
        additionalProperties = false,
    });

    private readonly IntelligentlyDbContext _db;

    public LookupKnowledgeTool(IntelligentlyDbContext db)
    {
        _db = db;
    }

    public ToolDescriptor Descriptor { get; } = new(
        ToolName,
        "Searches the course knowledge base (FAQs, known lab errors, misconceptions, best practices) and returns the best matching entries as JSON.",
        Schema,
        ToolRiskLevel.ReadOnly);

    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
    {
        if (call.Input.ValueKind != JsonValueKind.Object ||
            !call.Input.TryGetProperty("query", out var queryElement) ||
            queryElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(queryElement.GetString()))
        {
            return ToolResult.Error(call, "Invalid input: 'query' must be a non-empty string.");
        }

        var query = queryElement.GetString()!.Trim();
        int? courseId = call.Input.TryGetProperty("course_id", out var courseElement) && courseElement.ValueKind == JsonValueKind.Number
            ? courseElement.GetInt32()
            : null;

        var entries = await _db.AIKnowledgeEntries
            .AsNoTracking()
            .Where(e => e.IsActive)
            .Where(e => courseId == null || e.CourseId == null || e.CourseId == courseId)
            .Where(e => e.Question.Contains(query) || e.Answer.Contains(query) || (e.Tags != null && e.Tags.Contains(query)))
            .OrderByDescending(e => e.UpdatedAt)
            .Take(MaxResults)
            .Select(e => new { id = e.Id, entryType = e.EntryType, question = e.Question, answer = e.Answer, tags = e.Tags })
            .ToListAsync(ct);

        return ToolResult.Ok(call, JsonSerializer.Serialize(new { query, count = entries.Count, entries }));
    }
}
