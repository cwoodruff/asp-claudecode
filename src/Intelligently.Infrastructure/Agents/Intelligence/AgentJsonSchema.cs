using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

namespace Intelligently.Infrastructure.Agents.Intelligence;

/// <summary>
/// Exports JSON Schema from the C# contract records so one type defines the
/// structured-output format, the tool input schema and the deserialization target.
/// Objects are closed (<c>additionalProperties: false</c>) with every property
/// required, as strict tools and structured outputs expect.
/// </summary>
public static class AgentJsonSchema
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
        // Web defaults allow numbers-as-strings, which would export int as ["string","integer"].
        NumberHandling = JsonNumberHandling.Strict,
    };

    private static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = static (context, schema) =>
        {
            if (schema is not JsonObject obj) return schema;

            // Drop the "$schema" keyword the exporter adds at the root; the API does not want it.
            obj.Remove("$schema");

            if (obj["properties"] is JsonObject properties)
            {
                obj["additionalProperties"] = false;
                obj["required"] = new JsonArray(properties.Select(p => (JsonNode)p.Key).ToArray());
            }

            return obj;
        },
    };

    /// <summary>Schema for <typeparamref name="T"/> as a mutable node tree.</summary>
    public static JsonNode ForNode<T>() =>
        SerializerOptions.GetJsonSchemaAsNode(typeof(T), ExporterOptions);

    /// <summary>Schema for <typeparamref name="T"/> as the raw dictionary the SDK accepts.</summary>
    public static Dictionary<string, JsonElement> ForDictionary<T>() => ToDictionary(ForNode<T>());

    public static Dictionary<string, JsonElement> ToDictionary(JsonNode node)
    {
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    public static Dictionary<string, JsonElement> ToDictionary(JsonElement element) =>
        element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
