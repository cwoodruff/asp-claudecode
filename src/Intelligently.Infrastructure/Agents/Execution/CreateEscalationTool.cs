using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Entities.AI;
using Intelligently.Infrastructure.Data;

namespace Intelligently.Infrastructure.Agents.Execution;

/// <summary>
/// Mutating native tool: raises an <see cref="AIEscalation"/> for the instructors of the run's course.
/// Course-scoped on <c>course_id</c>, so the Control Plane allows it inside the run's course and asks for
/// approval elsewhere; covered by the idempotency barrier, so a retried step does not file it twice.
/// Exclusive: it writes through the stage's DbContext inside the stage transaction, so the escalation,
/// its idempotency receipt and the transition commit together or not at all.
/// </summary>
public sealed class CreateEscalationTool : NativeTool<CreateEscalationInput, CreateEscalationOutput>
{
    public const string ToolName = "create_escalation";

    private readonly IntelligentlyDbContext _db;

    public CreateEscalationTool(IntelligentlyDbContext db)
        : base(
            ToolName,
            "Escalates the learner's problem to a human instructor. Call it when the task needs a decision or knowledge the agent does not have, " +
            "or when the learner's situation needs a person. Returns the escalation id.",
            ToolRiskLevel.Mutating,
            courseScopeArgument: "course_id",
            exclusive: true)
    {
        _db = db;
    }

    protected override async Task<CreateEscalationOutput> ExecuteAsync(CreateEscalationInput input, ToolContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Question))
            throw new InvalidOperationException("'question' must not be empty.");
        if (string.IsNullOrWhiteSpace(input.Reason))
            throw new InvalidOperationException("'reason' must not be empty.");

        var escalation = new AIEscalation
        {
            AgentRunId = context.RunId,
            UserId = context.UserId,
            CourseId = input.CourseId,
            ModuleId = input.ModuleId,
            LearnerQuestion = input.Question.Trim(),
            EscalationReason = $"Agent run {context.RunId} ({context.TaskType}): {input.Reason.Trim()}",
            Priority = input.Priority.ToString(),
            Status = "New",
        };
        _db.AIEscalations.Add(escalation);
        // Flushes inside the stage transaction to obtain the id; the runner commits the transaction with the stage.
        await _db.SaveChangesAsync(ct);

        return new CreateEscalationOutput(escalation.Id, escalation.Status, escalation.Priority);
    }
}
