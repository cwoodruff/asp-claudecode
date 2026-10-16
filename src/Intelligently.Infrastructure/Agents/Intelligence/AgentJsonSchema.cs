using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

namespace Intelligently.Infrastructure.Agents.Intelligence;

/// <summary>
/// Exports JSON Schema from the C# contract records so one type defines the
/// structured-output format, the tool input schema and the deserialization target.
/// Objects are closed (<c>additionalProperties: false</c>) with every property
/// required, as strict tools and structured outputs expect. A task spec, which a client posts rather
/// than the model, is exported with <c>requireAll: false</c>: only non-nullable properties are required.
/// </summary>
public static class AgentJsonSchema
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
        // Web defaults allow numbers-as-strings, which would export int as ["string","integer"].
        NumberHandling = JsonNumberHandling.Strict,
        // The exporter marks the options read-only; without an explicit resolver that throws when the
        // first use of the options is a schema export rather than a (de)serialization.
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    private static readonly JsonSchemaExporterOptions ExporterOptions = Exporter(requireAll: true);
    private static readonly JsonSchemaExporterOptions OptionalExporterOptions = Exporter(requireAll: false);

    private static JsonSchemaExporterOptions Exporter(bool requireAll) => new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = (context, schema) =>
        {
            if (schema is not JsonObject obj) return schema;

            // Drop the "$schema" keyword the exporter adds at the root; the API does not want it.
            obj.Remove("$schema");

            // The exporter ignores [Description]; copy it from the property so the model sees it.
            if (context.PropertyInfo?.AttributeProvider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
                    .OfType<DescriptionAttribute>().FirstOrDefault() is { } description && !obj.ContainsKey("description"))
            {
                obj["description"] = description.Description;
            }

            if (obj["properties"] is JsonObject properties)
            {
                obj["additionalProperties"] = false;
                obj["required"] = new JsonArray(properties
                    .Where(p => requireAll || !IsNullable(p.Value))
                    .Select(p => (JsonNode)p.Key)
                    .ToArray());
            }

            return obj;
        },
    };

    /// <summary>True when a property schema admits null (<c>type: [..., "null"]</c>), which makes it optional in a task spec.</summary>
    private static bool IsNullable(JsonNode? property) =>
        property is JsonObject o && o["type"] is JsonArray types && types.Any(t => t is JsonValue v && v.TryGetValue<string>(out var s) && s == "null");

    /// <summary>
    /// Schema for <typeparamref name="T"/> as a mutable node tree. With <paramref name="requireAll"/> false
    /// only non-nullable properties are required (task specs); the default requires every property (tools, structured outputs).
    /// </summary>
    public static JsonNode ForNode<T>(bool requireAll = true) =>
        SerializerOptions.GetJsonSchemaAsNode(typeof(T), requireAll ? ExporterOptions : OptionalExporterOptions);

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
