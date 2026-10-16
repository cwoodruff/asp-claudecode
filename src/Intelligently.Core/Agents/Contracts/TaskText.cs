using System.Text;
using System.Text.Json;
using Intelligently.Core.Agents.Entities;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>
/// The free text of a task, used for retrieval and episode similarity: the task type followed by
/// every string and number value of the task spec, in document order.
/// </summary>
public static class TaskText
{
    public static string From(AgentRun run) => From(run.TaskType, run.TaskSpec);

    public static string From(string taskType, string taskSpecJson)
    {
        var sb = new StringBuilder(taskType);
        try
        {
            using var doc = JsonDocument.Parse(taskSpecJson);
            Append(sb, doc.RootElement);
        }
        catch (JsonException)
        {
            sb.Append(' ').Append(taskSpecJson);
        }
        return sb.ToString().Trim();
    }

    /// <summary>An integer property of the task spec under any of <paramref name="names"/>, or null.</summary>
    public static int? IntegerOf(string taskSpecJson, params string[] names)
    {
        try
        {
            using var doc = JsonDocument.Parse(taskSpecJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id))
                    return id;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static void Append(StringBuilder sb, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) Append(sb, property.Value);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Append(sb, item);
                break;
            case JsonValueKind.String:
                sb.Append(' ').Append(element.GetString());
                break;
            case JsonValueKind.Number:
                sb.Append(' ').Append(element.GetRawText());
                break;
        }
    }
}
