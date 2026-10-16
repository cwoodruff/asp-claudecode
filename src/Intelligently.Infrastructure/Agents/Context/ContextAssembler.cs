using System.Text;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Infrastructure.Agents.Context;

/// <summary>
/// Phase 1 stub of the Context Plane: a frozen system prompt, the sorted tool list,
/// and one user message carrying the task, the current plan and the run transcript.
/// Token budgeting, retrieval, episodic memory and compaction arrive in Phase 3.
/// </summary>
public sealed class ContextAssembler : IContextAssembler
{
    public const string SystemPrompt =
        "You are the Intelligently autonomous tutoring agent. You help learners with course tasks by " +
        "planning carefully, using only the tools you are given, verifying your own work, and explaining " +
        "results at the level of the learner. Never invent tool results. When a plan step names a tool, call " +
        "that tool with arguments that match its schema. When a step is reasoning only, answer in plain text.";

    private readonly IntelligentlyDbContext _db;
    private readonly IToolRegistry _tools;

    public ContextAssembler(IntelligentlyDbContext db, IToolRegistry tools)
    {
        _db = db;
        _tools = tools;
    }

    public async Task<PromptEnvelope> BuildAsync(AgentRun run, AgentStage stage, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Task type: {run.TaskType}");
        sb.AppendLine($"Course id: {run.CourseId}");
        sb.AppendLine("Task specification (JSON):");
        sb.AppendLine(run.TaskSpec);
        sb.AppendLine();

        var plan = await _db.AgentPlans
            .AsNoTracking()
            .Where(p => p.RunId == run.Id && p.Revision == run.PlanRevision)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (stage == AgentStage.Plan)
        {
            sb.AppendLine("Available tools:");
            foreach (var tool in _tools.Descriptors)
                sb.AppendLine($"- {tool.Name}: {tool.Description}");
            sb.AppendLine();
            if (run.PlanRevision > 0)
                sb.AppendLine($"This is plan revision {run.PlanRevision}. The previous attempt failed: {run.StatusReason}");
            sb.AppendLine("Produce a short, ordered plan. Each step names exactly one tool from the list, or null for a reasoning-only step, and states how success will be verified.");
        }
        else if (plan is not null)
        {
            sb.AppendLine($"Goal: {plan.Goal}");
            sb.AppendLine("Plan:");
            foreach (var step in plan.Steps)
                sb.AppendLine($"  {step.Index}. {step.Description} [tool: {step.Tool ?? "none"}] (success: {step.SuccessCriteria})");
            sb.AppendLine();

            var priorSteps = await _db.AgentSteps
                .AsNoTracking()
                .Where(s => s.RunId == run.Id && s.PlanRevision == run.PlanRevision && s.Status != AgentStep.StatusRunning)
                .OrderBy(s => s.StartedAt)
                .ToListAsync(ct);

            if (priorSteps.Count > 0)
            {
                sb.AppendLine("Transcript of earlier steps:");
                foreach (var prior in priorSteps)
                {
                    sb.AppendLine($"- step {prior.PlanStepIndex} attempt {prior.Attempt}: {prior.Status}. {prior.VerifierMessage}");
                    if (!string.IsNullOrWhiteSpace(prior.AssistantText)) sb.AppendLine($"  assistant: {prior.AssistantText}");
                    if (!string.IsNullOrWhiteSpace(prior.ToolResultsJson)) sb.AppendLine($"  results: {prior.ToolResultsJson}");
                }
                sb.AppendLine();
            }

            if (run.CurrentStepIndex < plan.Steps.Count)
            {
                var current = plan.Steps[run.CurrentStepIndex];
                sb.AppendLine($"Now execute step {current.Index}: {current.Description}");
                sb.AppendLine(current.Tool is not null
                    ? $"Call the tool '{current.Tool}' to do this."
                    : "This step needs no tool; answer in plain text.");
            }
        }

        return new PromptEnvelope(SystemPrompt, _tools.Descriptors, [new PromptMessage("user", sb.ToString())]);
    }
}
