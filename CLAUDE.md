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

This is **Intelligently**, an ASP.NET Core 10 AI-powered tutoring platform using Claude (Anthropic SDK). It follows a layered architecture:

- **Intelligently.Core** — Domain entities and interfaces. No external dependencies. Contains `IAnthropicService` and all AI domain entities (AIConversation, AIMessage, AIEscalation, AIKnowledgeEntry, AIAnalyticsEvent) in `Entities/AI/`.
- **Intelligently.Infrastructure** — Data access (EF Core 10 + SQL Server) and external services. `AnthropicService` wraps the Anthropic SDK with Polly retry (exponential backoff, 3 attempts). `IntelligentlyDbContext` extends `IdentityDbContext`.
- **Intelligently.Web** — Main Razor Pages app. Configures rate limiting (per-user sliding window: 10 msgs/min for chat, 120 reqs/min global) and DI for Anthropic service.
- **Intelligently.Admin** — Admin portal (Razor Pages).

Dependency flow: Web/Admin → Infrastructure → Core.

## Key Patterns

- **AI integration**: `IAnthropicService` (Core) / `AnthropicService` (Infrastructure) supports both streaming (`IAsyncEnumerable<string>`) and non-streaming completions. Default models: Haiku for completions, Sonnet for streaming.
- **Escalation workflow**: AI conversations can escalate to instructors; resolved escalations can feed back into the knowledge base (`AIKnowledgeEntry`).
- **Rate limiting**: Built-in ASP.NET Core 10 rate limiter (no external packages). Named policy `chat` with sliding window.
- **Frontend**: htmx (jQuery and Bootstrap were removed).

## Required Runtime Configuration

- `ConnectionStrings:DefaultConnection` — SQL Server connection string
- `Anthropic:ApiKey` — Anthropic API key (validated at startup)

## Testing Stack

xUnit, Moq, FluentAssertions, coverlet for coverage.
