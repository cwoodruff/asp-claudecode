using System.Text.Json;
using Intelligently.Core.Agents.Entities;
using Intelligently.Infrastructure.Agents;

namespace Intelligently.Web.Agents;

/// <summary>JSON request to create a run.</summary>
public sealed record CreateRunRequest(string? UserId, int CourseId, string TaskType, JsonElement TaskSpec);

/// <summary>JSON view of a run for API clients.</summary>
public sealed record RunResponse(Guid Id, string Stage, string Status, string? StatusReason, int PlanRevision, int CurrentStepIndex, DateTime CreatedAt, DateTime? CompletedAt)
{
    public static RunResponse From(AgentRun run) => new(
        run.Id, run.Stage.ToString(), run.Status, run.StatusReason, run.PlanRevision, run.CurrentStepIndex, run.CreatedAt, run.CompletedAt);
}

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/agent/runs");

        // POST /agent/runs — create a run and enqueue it. GET /agent/runs/{id} is the
        // Razor page Pages/Agent/Run.cshtml, which returns the htmx partial for HX-Request calls.
        group.MapPost("/", async (CreateRunRequest request, HttpContext http, AgentRunService runs, CancellationToken ct) =>
        {
            var userId = http.User.Identity?.IsAuthenticated == true
                ? http.User.Identity.Name
                : request.UserId;

            if (string.IsNullOrWhiteSpace(userId))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["userId"] = ["A user id is required."] });
            if (string.IsNullOrWhiteSpace(request.TaskType))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["taskType"] = ["A task type is required."] });
            if (request.TaskSpec.ValueKind != JsonValueKind.Object)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["taskSpec"] = ["The task spec must be a JSON object."] });

            var run = await runs.CreateAsync(userId, request.CourseId, request.TaskType.Trim(), request.TaskSpec.GetRawText(), ct);
            return Results.Created($"/agent/runs/{run.Id}", RunResponse.From(run));
        })
        .RequireRateLimiting("chat")
        .WithName("CreateAgentRun");

        return app;
    }
}
