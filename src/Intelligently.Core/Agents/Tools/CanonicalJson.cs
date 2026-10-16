using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Intelligently.Core.Agents.Tools;

/// <summary>
/// Canonical JSON for hashing tool arguments: object keys sorted ordinally at every level,
/// no whitespace, so two calls that differ only in property order hash the same.
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            Write(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Upper-case SHA-256 hex of the canonical form (64 characters).</summary>
    public static string Sha256(JsonElement element) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(element))));

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
