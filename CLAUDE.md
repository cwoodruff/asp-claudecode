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
- `Agents` — `DefaultModel`, `CheapModel`, `Budgets` (`ReplansLeft`, `RetriesLeft`, `ToolCallsLeft`, `TokensLeft`)

## Autonomous Agent Runtime (Phase 1)

Spec: `docs/autonomous-agent-plan.md` (five planes, seven-stage conditional state graph, transition table in section 2.2).

- **Core/Agents** — `Contracts/` (IReasoningEngine, IContextAssembler, IToolDispatcher, IPolicyDecisionPoint, IEvidenceSink, IRunQueue), `Entities/` (AgentRun, AgentStep, AgentPlan, AgentTrace, AgentEpisode), `StateGraph/` (AgentStage, StageOutcome, StageTransitions table, RecoveryBudget owned type, IStageHandler), `Tools/` (ToolDescriptor, ToolCall, ToolResult, ToolRiskLevel, IToolHandler, IToolRegistry), `AgentsOptions`.
- **Infrastructure/Agents** — `StateGraph/StateGraphRunner` executes one stage per `StepAsync`, rejects any edge not in `StageTransitions`, commits after every stage; `StateGraph/Handlers/` one handler per stage; `RunQueue` (bounded `Channel<Guid>`); `Intelligence/AnthropicReasoningEngine` (structured output from the `AgentPlan` record schema, strict tools + `tool_choice: auto`, adaptive thinking); `Execution/` (ToolRegistry, ToolDispatcher, `lookup_knowledge`); `Control/PolicyDecisionPoint` (Phase 1: ReadOnly allowed, everything else denied); `Evidence/OtelEvidenceSink` (spans + append-only AgentTrace rows); `Context/ContextAssembler` (stub). Register everything with `services.AddAgentRuntime(configuration)`.
- **Web** — `POST /agent/runs` (JSON, minimal API), `GET /agent/runs/{id}` (Razor page; returns the `_RunStatus` htmx partial when the `HX-Request` header is present), `/agent` demo form, `RunWorker` background service.
- **AIEscalation** now has a nullable `ConversationId` and an `AgentRunId` so agent runs can escalate without a chat conversation.
- Migrations live in `src/Intelligently.Infrastructure/Data/Migrations`; add one with `dotnet ef migrations add <Name> --project src/Intelligently.Infrastructure --startup-project src/Intelligently.Infrastructure --output-dir Data/Migrations` (a design-time factory supplies the context).
- Tests: `tests/Intelligently.UnitTests/Agents` run the real runtime on SQLite in-memory with a mocked `IReasoningEngine`; `tests/Intelligently.IntegrationTests/Agents` hosts the Web app with `WebApplicationFactory` the same way.

## Testing Stack

xUnit, Moq, FluentAssertions, coverlet for coverage. Agent tests use `Microsoft.EntityFrameworkCore.Sqlite` (in-memory) and `Microsoft.AspNetCore.Mvc.Testing`.
