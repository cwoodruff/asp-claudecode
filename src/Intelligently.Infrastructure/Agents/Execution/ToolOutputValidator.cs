using System.Collections.Concurrent;
using System.Text.Json;
using Intelligently.Core.Agents.Tools;
using Json.Schema;

namespace Intelligently.Infrastructure.Agents.Execution;

/// <summary>
/// Validates a successful tool result against the tool's declared output schema (plan section 2.4,
/// contract 1) so Verify only ever sees results of the declared shape. A result that is not JSON or
/// does not validate becomes an <c>is_error</c> result with <see cref="ToolResult.ErrorKindOutputInvalid"/>.
/// Compiled schemas are cached by their canonical text.
/// </summary>
public sealed class ToolOutputValidator
{
    private const int MaxErrorDetails = 5;

    private readonly ConcurrentDictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);

    public ToolResult Validate(ToolDescriptor descriptor, ToolCall call, ToolResult result)
    {
        if (result.IsError || descriptor.OutputSchema is not { } schemaElement)
            return result;

        JsonElement instance;
        if (result.Payload is { } payload)
        {
            instance = payload;
        }
        else
        {
            try
            {
                using var document = JsonDocument.Parse(result.Content);
                instance = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                return ToolResult.OutputInvalid(call, $"the result is not valid JSON ({ex.Message}).");
            }
        }

        var schema = _schemas.GetOrAdd(CanonicalJson.Serialize(schemaElement), static text => JsonSchema.FromText(text));
        var evaluation = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (evaluation.IsValid)
            return result with { Payload = instance };

        var errors = Errors(evaluation).Take(MaxErrorDetails).ToList();
        var detail = errors.Count == 0 ? "the result does not match the schema" : string.Join("; ", errors);
        return ToolResult.OutputInvalid(call, $"{detail}.");
    }

    private static IEnumerable<string> Errors(EvaluationResults results)
    {
        var nodes = results.Details is { Count: > 0 } details ? details : [results];
        foreach (var node in nodes)
        {
            if (node.Errors is null) continue;
            foreach (var (keyword, message) in node.Errors)
                yield return $"{node.InstanceLocation} {keyword}: {message}";
        }
    }
}
