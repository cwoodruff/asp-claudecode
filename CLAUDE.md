# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run Commands

```bash
# Build entire solution
dotnet build

# Run web app (http://localhost:5155, https://localhost:7038)
dotnet run --project src/Intelligently.Web

# Run admin app (http://localhost:5001, https://localhost:7263)
dotnet run --project src/Intelligently.Admin

# Run all tests
dotnet test

# Run a single test project
dotnet test tests/Intelligently.UnitTests/
dotnet test tests/Intelligently.IntegrationTests/

# Run tests with coverage
dotnet test /p:CollectCoverage=true
```

## Architecture

This is **Intelligently**, an ASP.NET Core 10 AI-powered tutoring platform using Claude (official `Anthropic` NuGet SDK). It follows a layered architecture:

- **Intelligently.Core** — Domain entities and interfaces. No external dependencies. Contains `IAnthropicService` and all AI domain entities (AIConversation, AIMessage, AIEscalation, AIKnowledgeEntry, AIAnalyticsEvent) in `Entities/AI/`.
- **Intelligently.Infrastructure** — Data access (EF Core 10 + SQL Server) and external services. `AnthropicService` wraps the official Anthropic SDK (`client.Messages.Create` / `CreateStreaming`); the SDK's built-in retry handles 408/409/429/5xx, so there is no Polly wrapper. A `refusal` stop reason throws `AIRefusalException` (Core). `IntelligentlyDbContext` extends `IdentityDbContext`.
- **Intelligently.Web** — Main Razor Pages app. Configures rate limiting (per-user sliding window: 10 msgs/min for chat, 120 reqs/min global), DI for the Anthropic client/service, and OpenTelemetry (ActivitySource `Intelligently.Agents`; console exporter in Development, OTLP via `Otel:Endpoint` otherwise).
- **Intelligently.Admin** — Admin portal (Razor Pages).

Dependency flow: Web/Admin → Infrastructure → Core.

## Key Patterns

- **AI integration**: `IAnthropicService` (Core) / `AnthropicService` (Infrastructure) supports both streaming (`IAsyncEnumerable<string>`) and non-streaming completions. Default model for both paths is `claude-opus-5-5` (adaptive thinking); `AnthropicService.CheapModel` (`claude-haiku-5-5`) is for cheap routes.
- **Escalation workflow**: AI conversations can escalate to instructors; resolved escalations can feed back into the knowledge base (`AIKnowledgeEntry`).
- **Rate limiting**: Built-in ASP.NET Core 10 rate limiter (no external packages). Named policy `chat` with sliding window.
- **Frontend**: htmx (jQuery and Bootstrap were removed).

## Required Runtime Configuration

- `ConnectionStrings:DefaultConnection` — SQL Server connection string
- `Anthropic:ApiKey` — Anthropic API key (validated at startup)
- `Otel:Endpoint` — OTLP collector endpoint (non-Development only)
- `Agents` — `DefaultModel`, `CheapModel`, `Budgets` (`ReplansLeft`, `RetriesLeft`, `ToolCallsLeft`, `TokensLeft`, `PromptTokens` plus per-stage `StagePromptTokens`), `ApprovalSweepSeconds`, `Policy:Rules` (Control Plane rules as JSON; `PolicyRule.Defaults` apply when empty), `Context` (`KnowledgeEntries`, `Episodes`, `EpisodeCandidates`, `CompactAfterTokens`, `SummaryMaxChars`, `SummaryInputMaxChars`)

## Autonomous Agent Runtime (Phases 1–3)

Spec: `docs/autonomous-agent-plan.md` (five planes, seven-stage conditional state graph, transition table in section 2.2).

- **Core/Agents** — `Contracts/` (IReasoningEngine, IContextAssembler + PromptEnvelope/PromptMessage/PromptContent/PromptSection, IKnowledgeRetriever, IEpisodicMemoryStore, IPromptTokenCounter, IEpisodeSummarizer, TaskText, IToolDispatcher, IPolicyDecisionPoint + PolicyRequest/PolicyDecision, PolicyRule, RunPrincipal, IIdempotencyBarrier, IToolRateLimiter, IApprovalGate, IEvidenceSink, IRunQueue), `Entities/` (AgentRun with an owned `Principal`, AgentStep with `ResponseContentJson`/`PromptTokens`, AgentPlan with `ResponseContentJson`, AgentTrace, AgentEpisode with `TaskText`/`Embedding`, AgentApproval, IdempotencyRecord), `StateGraph/` (AgentStage, StageOutcome, StageTransitions table, RecoveryBudget owned type, IStageHandler), `Tools/` (ToolDescriptor with optional `CourseScopeArgument`, ToolCall, ToolResult with `ErrorKind`, ToolRiskLevel, CanonicalJson, IToolHandler, IToolRegistry), `AgentsOptions`.
- **Infrastructure/Agents** — `StateGraph/StateGraphRunner` executes one stage per `StepAsync`, rejects any edge not in `StageTransitions`, commits after every stage; `StateGraph/Handlers/` one handler per stage including `AwaitingApprovalHandler`; `RunQueue` (bounded `Channel<Guid>`); `Intelligence/AnthropicReasoningEngine` (beta messages endpoint; structured output from the `AgentPlan` record schema; the tool list is sent on every stage with `tool_choice: none` for Plan/Judge and `auto` for Act; adaptive thinking; a `compaction` stop continues the turn with the response content appended) and `Intelligence/PromptEnvelopeMapper` (envelope → SDK types, cache breakpoints as `cache_control`); `Execution/` (ToolRegistry, ToolDispatcher, `lookup_knowledge` over the retriever); `Control/` (rule-driven `PolicyDecisionPoint`, `IdempotencyBarrier`, `ToolRateLimiter`, `ApprovalGate`); `Evidence/OtelEvidenceSink` (spans + append-only AgentTrace rows); `Context/` (see Context Plane below). Register everything with `services.AddAgentRuntime(configuration)`.
- **Control Plane** — `PolicyDecisionPoint` evaluates `Agents:Policy:Rules` in a fixed order: identity (no principal → Deny; course-scoped call outside the principal's enrolment → Deny; role requirements), risk gate (ReadOnly Allow, Mutating allowed inside the run's course, Destructive/External RequireApproval; a recorded approval for the same run+tool+args hash satisfies the gate; no matching rule fails closed to RequireApproval), rate limit (`ToolRateLimiter`, sliding windows per run and per user from the rules, separate from the HTTP limiter; exceeded → Deny), idempotency (covered calls keyed on `(RunId, ToolName, SHA-256(canonical args))`; a replay returns the stored result without executing; only successful results are stored). Deny is final for that call: Verify sets `RecoveryReason = PolicyDenied` and Recover replans without retrying. RequireApproval parks the run (step status `AwaitingApproval`, `AgentApproval` row, `AIEscalation` for instructors); `IApprovalGate.DecideAsync` records the decision as an `Approval` trace, resolves the escalation, sets the run back to Running and re-enqueues it. Approved resumes the same Act step with the parked calls (no new model turn); denied goes to Recover then Plan with `PlanRevision + 1`.
- **Context Plane** — `ContextAssembler.BuildAsync(run, stage)` builds the `PromptEnvelope` stable-to-volatile: frozen system block (cache breakpoint) → name-sorted tools (breakpoint on the last) → `Knowledge` message (header block + one block per retrieved entry) → `Episodic` message (header + one block per episode) → `Transcript` (task; per revision the plan as an assistant turn replayed from `AgentPlan.ResponseContentJson` plus a user acknowledgement; per finished step the assistant turn replayed verbatim from `AgentStep.ResponseContentJson`, else synthesized from text + tool calls, followed by a user message with one `tool_result` per `tool_use` and the verifier verdict; breakpoint on the last transcript block) → the stage instruction as a `system` message inside `Messages`, never in the top-level system field. `PromptEnvelope.SerializePrefix()`/`PrefixHash()` cover system + tools. `TokenBudgeter` counts with `IPromptTokenCounter` (`AnthropicTokenCounter` → `client.Beta.Messages.CountTokens`, falling back to `EstimatingTokenCounter`) and trims episodic entries, then the episodic block, then knowledge entries, then the knowledge block until under `Budgets:PromptTokens` (or the stage override); the count lands on `PromptEnvelope.InputTokens` and `AgentStep.PromptTokens`. `KnowledgeRetriever` uses `FREETEXT` on SQL Server when the Phase 3 migration created the full-text index (guarded on `IsFullTextInstalled`; LocalDB has none), else a `LIKE` filter on the query terms; candidates are scored in memory (question 3, tags 2, answer 1) and scoped by course (plus course-less entries) and module. `EpisodicMemoryStore` writes one `AgentEpisode` per completed run (`TaskText` from `TaskText.From(run)`, `Embedding` stored as JSON but not filled) and recalls same-task-type episodes with trigram similarity, this learner first. `Compactor` is both modes: `ServerSideConfig` (`compact-2026-01-12`, input-token trigger at `Context:CompactAfterTokens`, requested once the counted prompt reaches that size) and `IEpisodeSummarizer` (Haiku at low effort, `ExtractiveSummarizer` fallback). `CacheHealthMonitor` records cache read/creation token counters on the `Intelligently.Agents` meter and counts/warns when a run repeats a prefix that was cached and reads zero cache tokens. Test hosts register `EstimatingTokenCounter` and `ExtractiveSummarizer` so no counting or summarizing reaches the network.
- **Recover** — hard budget exhausted → escalate; oscillation (current plan hash equals one of the previous three revisions, or the current step failed twice with the same verifier message) → escalate; `StepFailed` with retries left → Act (RetriesLeft − 1); otherwise replans left → Plan (ReplansLeft − 1, PlanRevision + 1); else escalate.
- **Authentication** — both hosts call `services.AddIntelligentlyIdentity()` (`Infrastructure/Identity`): ASP.NET Core Identity (`IdentityUser`/`IdentityRole`, cookie) over `IntelligentlyDbContext`, login at `/Account/Login` (`/Account/Logout`, `/Account/AccessDenied`; accounts are provisioned out of band, there is no registration page), plus the `AgentAuthorization.InstructorPolicy` ("Instructor" role) policy. Browser requests that fail are redirected; JSON (`Content-Type`/`Accept: application/json`) and htmx (`HX-Request`) requests get a bare 401/403. `IntelligentlyClaims.ToRunPrincipal(ClaimsPrincipal)` is the only way a `RunPrincipal` is built from a caller: user id = `NameIdentifier`, roles = role claims, course scope = `intelligently:course` claims (one per enrolled course id, stored as Identity user claims until an enrolment entity exists). Nothing in a request body or form contributes to the principal.
- **Web** — every agent route requires an authenticated caller. `POST /agent/runs` (JSON, minimal API; body is `courseId`, `taskType`, `taskSpec` — a `userId` is ignored; 403 when the caller is not enrolled in the course), `GET /agent/runs/{id}` (Razor page; returns the `_RunStatus` htmx partial when the `HX-Request` header is present, polling while Running or Waiting; only the owning learner or an Instructor may see it, others get 403), `/agent` demo form (runs as the signed-in user, course must be in their enrolment), `RunWorker` background service (also re-enqueues Running runs at startup and sweeps for approvals decided in Admin every `Agents:ApprovalSweepSeconds`).
- **Admin** — same DbContext, Anthropic client, `AddAgentRuntime` and `AddIntelligentlyIdentity` registrations as Web (no worker). `/approvals`, `/runs` and `/runs/{id}` carry `[Authorize(Policy = AgentAuthorization.InstructorPolicy)]`. `/approvals` lists pending `AgentApproval` rows with approve/deny htmx forms (`?handler=Approve|Deny`, antiforgery token required) and records `DecidedBy` as the signed-in user's name; `/runs` and `/runs/{id}` are read-only (run, plans, approvals, steps, traces in order).
- **AIEscalation** now has a nullable `ConversationId` and an `AgentRunId` so agent runs can escalate without a chat conversation.
- Migrations live in `src/Intelligently.Infrastructure/Data/Migrations`; add one with `dotnet ef migrations add <Name> --project src/Intelligently.Infrastructure --startup-project src/Intelligently.Infrastructure --output-dir Data/Migrations` (a design-time factory supplies the context).
- Tests: `tests/Intelligently.UnitTests/Agents` run the real runtime on SQLite in-memory with a mocked `IReasoningEngine` (Context Plane: `ContextAssemblerTests`, `TokenBudgeterTests`, `EpisodicMemoryStoreTests`, `KnowledgeRetrieverTests`, `CompactionTests`, `CacheHealthMonitorTests`; the integration `StablePrefixEndpointTests` checks the prefix across turns in the Web host) (`StubTool` and `RecordingRunQueue` in `ControlPlaneStubs.cs` for Control Plane scenarios); `tests/Intelligently.IntegrationTests/Agents` hosts the Web app and `tests/Intelligently.IntegrationTests/Admin` hosts the Admin app with `WebApplicationFactory` the same way (the type parameter names a type from the entry assembly because both apps declare `Program`). Both factories register `TestAuthHandler` (`TestAuthentication.cs`) as the default scheme: a request is authenticated from the `X-Test-User`/`X-Test-Roles`/`X-Test-Courses` headers (`client.AsLearner("id", courses)`, `AsInstructor`, `As(...)`), or anonymous without them; challenges and forbids are forwarded to Identity's cookie scheme so the production redirect/401/403 behaviour is what tests see (`AgentAuthorizationTests`, `AdminAuthorizationTests`).

## Testing Stack

xUnit, Moq, FluentAssertions, coverlet for coverage. Agent tests use `Microsoft.EntityFrameworkCore.Sqlite` (in-memory) and `Microsoft.AspNetCore.Mvc.Testing`.
