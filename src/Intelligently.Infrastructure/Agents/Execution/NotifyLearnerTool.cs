using System.Text.Json;
using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Entities.AI;
using Intelligently.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Intelligently.Infrastructure.Agents.Execution;

/// <summary>
/// External native tool: sends a message to the learner on a channel outside the platform's own data,
/// so the Control Plane asks for approval before every call. For now the notification is recorded as an
/// <see cref="AIAnalyticsEvent"/> (<c>LearnerNotified</c>) with the message in its metadata.
/// TODO: deliver through the real channel (in-app inbox, e-mail) once one exists; keep the event as the audit record.
/// </summary>
public sealed class NotifyLearnerTool : NativeTool<NotifyLearnerInput, NotifyLearnerOutput>
{
    public const string ToolName = "notify_learner";
    public const string EventType = "LearnerNotified";

    private readonly IServiceScopeFactory _scopes;

    public NotifyLearnerTool(IServiceScopeFactory scopes)
        : base(
            ToolName,
            "Sends a message to the learner outside this run (in-app or e-mail). Call it only when the learner must be told something " +
            "they would otherwise miss, such as an instructor taking over. Needs instructor approval.",
            ToolRiskLevel.External,
            courseScopeArgument: "course_id")
    {
        _scopes = scopes;
    }

    protected override async Task<NotifyLearnerOutput> ExecuteAsync(NotifyLearnerInput input, ToolContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Message))
            throw new InvalidOperationException("'message' must not be empty.");

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntelligentlyDbContext>();

        var analyticsEvent = new AIAnalyticsEvent
        {
            EventType = EventType,
            UserId = context.UserId,
            CourseId = input.CourseId,
            Metadata = JsonSerializer.Serialize(new
            {
                runId = context.RunId,
                taskType = context.TaskType,
                channel = input.Channel.ToString(),
                message = input.Message.Trim(),
            }),
        };
        db.AIAnalyticsEvents.Add(analyticsEvent);
        await db.SaveChangesAsync(ct);

        return new NotifyLearnerOutput(analyticsEvent.Id, input.Channel.ToString(), "Recorded");
    }
}
