using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;

namespace Intelligently.Core.Agents.Tools;

/// <summary>
/// What a tool may know about the run it executes for: enough to attribute side effects (the run,
/// the learner, the course) without handing the handler the mutable <see cref="AgentRun"/>.
/// </summary>
public sealed record ToolContext(Guid RunId, string UserId, int CourseId, string TaskType, RunPrincipal Principal)
{
    public static ToolContext For(AgentRun run) => new(run.Id, run.UserId, run.CourseId, run.TaskType, run.Principal);
}
