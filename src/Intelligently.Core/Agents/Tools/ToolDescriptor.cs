using System.Text.Json;

namespace Intelligently.Core.Agents.Tools;

/// <summary>
/// Describes a tool the model may call. <see cref="InputSchema"/> is a full JSON Schema object.
/// <see cref="CourseScopeArgument"/> names the input property that carries a course id (for example
/// <c>course_id</c>) so the Control Plane can check it against the principal's enrolment and the
/// run's declared scope; null means the tool is not course-scoped. <see cref="OutputSchema"/>, when
/// present, is the JSON Schema the dispatcher validates every successful result against before Verify
/// sees it. <see cref="Strict"/> asks the provider to validate tool inputs against the schema; it is off
/// for schemas that do not meet the strict-mode rules (closed objects, every property required).
/// </summary>
public sealed record ToolDescriptor(
    string Name,
    string Description,
    JsonElement InputSchema,
    ToolRiskLevel RiskLevel,
    string? CourseScopeArgument = null,
    JsonElement? OutputSchema = null,
    bool Strict = true)
{
    /// <summary>The course id the call targets, when the tool is course-scoped and the argument is a number.</summary>
    public int? CourseIdOf(ToolCall call)
    {
        if (CourseScopeArgument is null || call.Input.ValueKind != JsonValueKind.Object) return null;
        if (!call.Input.TryGetProperty(CourseScopeArgument, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetInt32(out var id) ? id : null;
    }
}
