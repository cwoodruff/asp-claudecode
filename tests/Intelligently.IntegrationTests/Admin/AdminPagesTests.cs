using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tools;
using Intelligently.Core.Entities.AI;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.IntegrationTests.Admin;

/// <summary>The Admin approval queue lists pending calls and records decisions; the run page shows steps and traces.</summary>
public class AdminPagesTests : IClassFixture<AdminWebFactory>
{
    private readonly AdminWebFactory _factory;

    public AdminPagesTests(AdminWebFactory factory)
    {
        _factory = factory;
    }

    /// <summary>A run parked at AwaitingApproval with one pending approval, as the Web worker would leave it.</summary>
    private async Task<(AgentRun Run, AgentStep Step, AgentApproval Approval)> SeedParkedRunAsync(string userId)
    {
        var run = new AgentRun
        {
            UserId = userId,
            CourseId = 7,
            TaskType = "NoteCleanup",
            TaskSpec = """{"noteId":42}""",
            Stage = AgentStage.AwaitingApproval,
            Status = AgentRun.StatusWaiting,
            StatusReason = "Tool 'delete_notes' requires approval.",
            Principal = RunPrincipal.Learner(userId, 7),
        };
        var step = new AgentStep
        {
            RunId = run.Id,
            PlanRevision = 0,
            PlanStepIndex = 0,
            Attempt = 1,
            Status = AgentStep.StatusAwaitingApproval,
            ToolCallsJson = JsonSerializer.Serialize(new[] { new ToolCall("toolu_del", "delete_notes", JsonSerializer.SerializeToElement(new { course_id = 7, note_id = 42 })) }),
            ToolResultsJson = "[]",
        };
        var escalation = new AIEscalation { AgentRunId = run.Id, UserId = userId, CourseId = 7, LearnerQuestion = run.TaskSpec, EscalationReason = "needs approval", Priority = "Medium", Status = "New" };
        var approval = new AgentApproval
        {
            RunId = run.Id,
            StepId = step.Id,
            ToolName = "delete_notes",
            ToolCallJson = JsonSerializer.Serialize(new ToolCall("toolu_del", "delete_notes", JsonSerializer.SerializeToElement(new { course_id = 7, note_id = 42 }))),
            ArgsHash = new string('A', 64),
            Reason = "Rule 'destructive-approval': Destructive tool 'delete_notes' always requires approval.",
            Escalation = escalation,
        };
        var plan = new AgentPlan
        {
            RunId = run.Id,
            Revision = 0,
            Hash = new string('B', 64),
            Goal = "Remove the stale note.",
            Steps = [new PlanStep { Index = 0, Description = "Delete note 42.", Tool = "delete_notes", SuccessCriteria = "Gone." }],
        };
        var trace = new AgentTrace { RunId = run.Id, Stage = AgentStage.Act, Kind = AgentTrace.KindTransition, Payload = """{"from":"Act","to":"AwaitingApproval"}""" };

        await _factory.WithDbAsync(db =>
        {
            db.AgentRuns.Add(run);
            db.AgentSteps.Add(step);
            db.AgentPlans.Add(plan);
            db.AgentApprovals.Add(approval);
            db.AgentTraces.Add(trace);
            return Task.CompletedTask;
        });
        return (run, step, approval);
    }

    private static (string Token, string Cookie) ExtractAntiforgery(HttpResponseMessage response, string html)
    {
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        token.Should().NotBeEmpty("the approve/deny forms carry an antiforgery token");
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? string.Join("; ", cookies.Select(c => c.Split(';')[0]))
            : string.Empty;
        return (token, cookie);
    }

    [Fact]
    public async Task Approvals_page_lists_pending_calls_with_tool_arguments_and_reason()
    {
        var (run, _, approval) = await SeedParkedRunAsync("learner-list");
        var client = _factory.CreateClient().AsInstructor("instructor-1");

        var response = await client.GetAsync("/approvals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain($"id=\"approval-{approval.Id}\"");
        html.Should().Contain("data-approval-status=\"Pending\"");
        html.Should().Contain("<code>delete_notes</code>");
        html.Should().Contain("&quot;note_id&quot;: 42");
        html.Should().Contain("always requires approval");
        html.Should().Contain($"href=\"/runs/{run.Id}\"");
        html.Should().Contain("hx-post=\"/approvals?handler=Approve\"").And.Contain("hx-post=\"/approvals?handler=Deny\"");
    }

    [Fact]
    public async Task Approving_through_the_page_records_the_decision_and_hands_the_run_back_to_a_worker()
    {
        var (run, step, approval) = await SeedParkedRunAsync("learner-approve");
        var client = _factory.CreateClient(new() { HandleCookies = true }).AsInstructor("instructor-1");
        var page = await client.GetAsync("/approvals");
        var (token, _) = ExtractAntiforgery(page, await page.Content.ReadAsStringAsync());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/approvals?handler=Approve")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["id"] = approval.Id.ToString(),
                ["note"] = "Fine by me.",
            }),
        };
        request.Headers.Add("HX-Request", "true");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await response.Content.ReadAsStringAsync();
        row.Should().Contain("data-approval-status=\"Approved\"");
        row.Should().Contain("by instructor-1");
        row.Should().Contain("Fine by me.");
        row.Should().NotContain("hx-post", "a decided row has no buttons");

        await _factory.WithDbAsync(async db =>
        {
            var decided = await db.AgentApprovals.SingleAsync(a => a.Id == approval.Id);
            decided.Status.Should().Be(AgentApproval.StatusApproved);
            decided.DecidedBy.Should().Be("instructor-1");
            decided.DecisionNote.Should().Be("Fine by me.");

            var resumed = await db.AgentRuns.SingleAsync(r => r.Id == run.Id);
            resumed.Stage.Should().Be(AgentStage.AwaitingApproval);
            resumed.Status.Should().Be(AgentRun.StatusRunning, "the Web worker's sweep picks up Running runs at AwaitingApproval");

            (await db.AIEscalations.SingleAsync(e => e.Id == decided.EscalationId)).Status.Should().Be("Resolved");
            (await db.AgentTraces.CountAsync(t => t.RunId == run.Id && t.Kind == AgentTrace.KindApproval && t.StepId == step.Id)).Should().Be(1);
        });
    }

    [Fact]
    public async Task Denying_through_the_page_marks_the_approval_denied()
    {
        var (run, _, approval) = await SeedParkedRunAsync("learner-deny");
        var client = _factory.CreateClient(new() { HandleCookies = true }).AsInstructor("instructor-1");
        var page = await client.GetAsync("/approvals");
        var (token, _) = ExtractAntiforgery(page, await page.Content.ReadAsStringAsync());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/approvals?handler=Deny")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["id"] = approval.Id.ToString(),
            }),
        };
        request.Headers.Add("HX-Request", "true");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("data-approval-status=\"Denied\"");
        await _factory.WithDbAsync(async db =>
        {
            (await db.AgentApprovals.SingleAsync(a => a.Id == approval.Id)).Status.Should().Be(AgentApproval.StatusDenied);
            (await db.AgentRuns.SingleAsync(r => r.Id == run.Id)).Status.Should().Be(AgentRun.StatusRunning);
        });

        // A second decision on the same approval is refused.
        using var again = new HttpRequestMessage(HttpMethod.Post, "/approvals?handler=Approve")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["id"] = approval.Id.ToString() }),
        };
        again.Headers.Add("HX-Request", "true");
        (await client.SendAsync(again)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Run_page_shows_each_steps_model_call_with_cost_reasoning_summary_tool_payloads_and_sandbox_logs()
    {
        var run = new AgentRun
        {
            UserId = "learner-evidence", CourseId = 7, TaskType = "LabErrorDiagnosis", TaskSpec = """{"question":"CS0246"}""",
            Stage = AgentStage.Completed, Status = AgentRun.StatusCompleted, Principal = RunPrincipal.Learner("learner-evidence", 7),
            TotalInputTokens = 1620, TotalOutputTokens = 40, TotalCostUsd = 0.003980m, TraceId = new string('a', 32), RootSpanId = new string('b', 16),
            CompletedAt = DateTime.UtcNow,
        };
        var step = new AgentStep
        {
            RunId = run.Id, PlanRevision = 0, PlanStepIndex = 0, Attempt = 1, Status = AgentStep.StatusPassed, Stage = AgentStage.Act,
            AssistantText = "Reproducing the build first.", VerifierMessage = "Passed deterministic checks.", PromptTokens = 1500,
            ToolCallsJson = "[]", ToolResultsJson = "[]", CompletedAt = DateTime.UtcNow,
        };
        var modelCall = new AgentTrace
        {
            RunId = run.Id, StepId = step.Id, Stage = AgentStage.Act, Kind = AgentTrace.KindModelCall, Model = "claude-opus-5-5",
            InputTokens = 120, OutputTokens = 40, CacheReadInputTokens = 1000, CacheCreationInputTokens = 500, CostUsd = 0.003980m,
            ReasoningSummary = "The error points at a missing package reference; run the build to confirm.",
            StopReason = "tool_use", Effort = "medium", LatencyMs = 842.5,
            Payload = JsonSerializer.Serialize(new
            {
                envelope = new { system = new[] { new { text = "You are a tutor.", cacheBreakpoint = true } }, tools = new[] { "run_lab_code" }, messages = Array.Empty<object>(), prefixHash = new string('c', 64), promptTokens = 1500, compaction = false },
                response = "{\"id\":\"msg_01\"}",
                content = new object[] { new { type = "text", text = "Reproducing the build first." } },
                reasoningSummary = "The error points at a missing package reference; run the build to confirm.",
                stopReason = "tool_use", effort = "medium", latencyMs = 842.5, costUsd = 0.003980m,
            }),
        };
        var toolCall = new AgentTrace
        {
            RunId = run.Id, StepId = step.Id, Stage = AgentStage.Act, Kind = AgentTrace.KindToolCall, LatencyMs = 310,
            Payload = JsonSerializer.Serialize(new
            {
                call = new { Id = "toolu_lab", Name = "run_lab_code", input = new { language = "csharp", entrypoint = "Program.cs" } },
                decision = "Allow", decisionReason = "Rule 'read-only'", rule = "read-only", ruleKind = "Risk", riskLevel = "ReadOnly",
                replayed = false, coalesced = false, exclusive = false, parallel = false, latencyMs = 310,
                result = new { IsError = false, ErrorKind = (string?)null, Content = "{\"exit_code\":1}", payload = new { sandbox = "docker", exit_code = 1, stdout = "", stderr = "error CS0246: The type or namespace name 'Newtonsoft' could not be found" } },
            }),
        };
        var sandboxLog = new AgentTrace
        {
            RunId = run.Id, StepId = step.Id, Stage = AgentStage.Act, Kind = AgentTrace.KindSandboxLog, LatencyMs = 290,
            Payload = JsonSerializer.Serialize(new { call = new { Id = "toolu_lab", Name = "run_lab_code" }, sandbox = "docker", exitCode = 1, stdout = "", stderr = "error CS0246: The type or namespace name 'Newtonsoft' could not be found", timedOut = false, memoryExceeded = false, outputTruncated = false, durationSeconds = 0.29, error = (string?)null }),
        };
        var episode = new AgentEpisode { RunId = run.Id, UserId = run.UserId, CourseId = 7, TaskType = run.TaskType, TaskText = "LabErrorDiagnosis CS0246", Outcome = "Completed", Summary = "Diagnosed a missing package reference.", KnowledgeEntryId = 42 };
        await _factory.WithDbAsync(db =>
        {
            db.AgentRuns.Add(run);
            db.AgentSteps.Add(step);
            db.AgentTraces.AddRange(modelCall, toolCall, sandboxLog);
            db.AgentEpisodes.Add(episode);
            return Task.CompletedTask;
        });
        var client = _factory.CreateClient().AsInstructor("instructor-1");

        var html = await client.GetStringAsync($"/runs/{run.Id}");

        html.Should().Contain("data-run-cost=\"0.003980\"");
        html.Should().Contain($"data-trace-id=\"{new string('a', 32)}\"");
        html.Should().Contain($"data-step-id=\"{step.Id}\"").And.Contain("data-step-cost=\"0.003980\"");
        html.Should().Contain("data-cost-usd=\"0.003980\"");
        html.Should().Contain("The error points at a missing package reference; run the build to confirm.", "the reasoning summary is shown per step");
        html.Should().Contain("data-stop-reason=\"tool_use\"").And.Contain("data-effort=\"medium\"");
        html.Should().Contain("120 input · 1000 cache read · 500 cache creation · 40 output");
        html.Should().Contain("You are a tutor.", "the prompt envelope is shown");
        html.Should().Contain("&quot;id&quot;:&quot;msg_01&quot;", "the raw response is shown verbatim");
        html.Should().Contain("<code>run_lab_code</code>").And.Contain("Allow").And.Contain("ReadOnly");
        html.Should().Contain("data-sandbox-exit-code=\"1\"").And.Contain("could not be found");
        html.Should().Contain("data-episode-id=").And.Contain("data-knowledge-entry-id=\"42\"");
        html.Should().Contain($"hx-get=\"/runs/{run.Id}?handler=Trace&traceId={modelCall.Id}\"", "raw JSON is loaded on demand with htmx");
        html.Should().NotContain("hx-post", "the trace viewer is read-only");

        // The htmx handler returns the raw payload of one row of this run only.
        var raw = await client.GetAsync($"/runs/{run.Id}?handler=Trace&traceId={modelCall.Id}");
        raw.StatusCode.Should().Be(HttpStatusCode.OK);
        var partial = await raw.Content.ReadAsStringAsync();
        partial.Should().Contain($"data-trace-id=\"{modelCall.Id}\"").And.Contain("data-trace-kind=\"ModelCall\"").And.Contain("&quot;prefixHash&quot;");
        partial.Should().NotContain("<html", "it is a fragment");
        (await client.GetAsync($"/runs/{Guid.NewGuid()}?handler=Trace&traceId={modelCall.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A learner cannot read the raw payloads either.
        var learner = _factory.CreateClient(new() { AllowAutoRedirect = false }).AsLearner("learner-evidence", 7);
        (await learner.GetAsync($"/runs/{run.Id}?handler=Trace&traceId={modelCall.Id}")).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Escalations_page_lets_an_instructor_resolve_and_publish_to_the_knowledge_base()
    {
        var run = new AgentRun
        {
            UserId = "learner-esc", CourseId = 7, TaskType = "LabErrorDiagnosis", TaskSpec = """{"question":"CS0246"}""",
            Stage = AgentStage.Failed, Status = AgentRun.StatusFailed, StatusReason = "Escalated to instructor.", Principal = RunPrincipal.Learner("learner-esc", 7),
            CompletedAt = DateTime.UtcNow,
        };
        var escalation = new AIEscalation { AgentRunId = run.Id, UserId = run.UserId, CourseId = 7, LearnerQuestion = "CS0246 for Newtonsoft", EscalationReason = "could not recover", Priority = "High", Status = "New" };
        await _factory.WithDbAsync(db =>
        {
            db.AgentRuns.Add(run);
            db.AIEscalations.Add(escalation);
            return Task.CompletedTask;
        });
        var client = _factory.CreateClient(new() { HandleCookies = true }).AsInstructor("instructor-1");

        var page = await client.GetAsync("/escalations");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain($"id=\"escalation-{escalation.Id}\"").And.Contain("data-escalation-status=\"New\"").And.Contain("hx-post=\"/escalations?handler=Resolve\"");
        var (token, _) = ExtractAntiforgery(page, html);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/escalations?handler=Resolve")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["id"] = escalation.Id.ToString(),
                ["response"] = "Add the package reference.",
                ["addToKnowledgeBase"] = "true",
            }),
        };
        request.Headers.Add("HX-Request", "true");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await response.Content.ReadAsStringAsync();
        row.Should().Contain("data-escalation-status=\"Resolved\"").And.Contain("by instructor-1").And.Contain("Added to the knowledge base");
        row.Should().NotContain("hx-post");

        await _factory.WithDbAsync(async db =>
        {
            var entry = await db.AIKnowledgeEntries.SingleAsync(k => k.SourceEscalationId == escalation.Id);
            entry.Answer.Should().Be("Add the package reference.");
            entry.AuthoredByUserId.Should().Be("instructor-1");
            (await db.AgentEpisodes.SingleAsync(e => e.RunId == run.Id)).KnowledgeEntryId.Should().Be(entry.Id);
        });

        (await _factory.CreateClient(new() { AllowAutoRedirect = false }).AsLearner("learner-esc", 7).GetAsync("/escalations")).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Run_page_shows_the_run_its_steps_and_its_traces_in_order()
    {
        var (run, step, approval) = await SeedParkedRunAsync("learner-trace");
        var client = _factory.CreateClient().AsInstructor("instructor-1");

        var html = await client.GetStringAsync($"/runs/{run.Id}");

        html.Should().Contain($"Agent run <code>{run.Id}</code>");
        html.Should().Contain("data-stage=\"AwaitingApproval\"");
        html.Should().Contain("data-status=\"Waiting\"");
        html.Should().Contain("roles: Learner; courses: 7");
        html.Should().Contain("Revision 0");
        html.Should().Contain("Remove the stale note.");
        html.Should().Contain("data-step-status=\"AwaitingApproval\"");
        html.Should().Contain("data-trace-kind=\"Transition\"");
        html.Should().Contain("data-approval-status=\"Pending\"");
        html.Should().Contain("toolu_del");
        _ = step; _ = approval;

        (await client.GetAsync($"/runs/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var list = await client.GetStringAsync("/runs");
        list.Should().Contain($"href=\"/runs/{run.Id}\"");
    }
}
