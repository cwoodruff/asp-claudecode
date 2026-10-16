using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Tools;

namespace Intelligently.Infrastructure.Agents.Execution;

/// <summary>ReadOnly native tool: scored retrieval over active <c>AIKnowledgeEntry</c> rows through the Context Plane retriever.</summary>
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
            module_id = new { type = new[] { "integer", "null" }, description = "Restrict results to this module within the course, or null for all modules." },
        },
        required = new[] { "query", "course_id", "module_id" },
        additionalProperties = false,
    });

    private readonly IKnowledgeRetriever _retriever;

    public LookupKnowledgeTool(IKnowledgeRetriever retriever)
    {
        _retriever = retriever;
    }

    public ToolDescriptor Descriptor { get; } = new(
        ToolName,
        "Searches the course knowledge base (FAQs, known lab errors, misconceptions, best practices) and returns the best matching entries as JSON with a relevance score.",
        Schema,
        ToolRiskLevel.ReadOnly,
        CourseScopeArgument: "course_id");

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
        var courseId = IntegerArgument(call.Input, "course_id");
        var moduleId = IntegerArgument(call.Input, "module_id");

        var matches = await _retriever.SearchAsync(query, courseId, moduleId, MaxResults, ct);
        var entries = matches.Select(m => new
        {
            id = m.Entry.Id,
            entryType = m.Entry.EntryType,
            question = m.Entry.Question,
            answer = m.Entry.Answer,
            tags = m.Entry.Tags,
            score = m.Score,
        }).ToList();

        return ToolResult.Ok(call, JsonSerializer.Serialize(new { query, count = entries.Count, entries }));
    }

    private static int? IntegerArgument(JsonElement input, string name) =>
        input.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;
}
