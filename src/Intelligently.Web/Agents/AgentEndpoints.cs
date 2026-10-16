using System.Security.Claims;
using System.Text.Json;
using Intelligently.Core.Agents.Entities;
using Intelligently.Infrastructure.Agents;
using Intelligently.Infrastructure.Identity;

namespace Intelligently.Web.Agents;

/// <summary>
/// JSON request to create a run. There is deliberately no user id: the run acts as the authenticated
/// caller, and any <c>userId</c> property in the body is ignored by the deserializer.
/// </summary>
public sealed record CreateRunRequest(int CourseId, string TaskType, JsonElement TaskSpec);

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
        var group = app.MapGroup("/agent/runs").RequireAuthorization();

        // POST /agent/runs — create a run and enqueue it. GET /agent/runs/{id} is the
        // Razor page Pages/Agent/Run.cshtml, which returns the htmx partial for HX-Request calls.
        group.MapPost("/", async (CreateRunRequest request, ClaimsPrincipal user, AgentRunService runs, CancellationToken ct) =>
        {
            // The principal comes from the caller's claims only (id, roles, enrolled courses).
            var principal = user.ToRunPrincipal();
            if (principal is null)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.TaskType))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["taskType"] = ["A task type is required."] });
            if (request.TaskSpec.ValueKind != JsonValueKind.Object)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["taskSpec"] = ["The task spec must be a JSON object."] });
            if (!principal.CanAccessCourse(request.CourseId))
                return Results.Problem(
                    title: "Not enrolled",
                    detail: $"You are not enrolled in course {request.CourseId}.",
                    statusCode: StatusCodes.Status403Forbidden);

            var run = await runs.CreateAsync(principal.UserId, request.CourseId, request.TaskType.Trim(), request.TaskSpec.GetRawText(), principal, ct);
            return Results.Created($"/agent/runs/{run.Id}", RunResponse.From(run));
        })
        .RequireRateLimiting("chat")
        .WithName("CreateAgentRun");

        return app;
    }
}
