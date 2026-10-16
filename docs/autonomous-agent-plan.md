# Intelligently Autonomous Agent — Implementation Plan

This plan maps the **5-Plane Engineering Matrix** and the **7-Stage Conditional State Graph** onto the existing
Intelligently solution (ASP.NET Core 10, EF Core 10 + SQL Server, Razor Pages + htmx, official Anthropic C# SDK).

The target is an autonomous tutoring agent that can take a learner task (for example "my lab build fails with this
error"), plan, run tools in a sandbox, verify its own work, escalate to an instructor when it must, and feed what it
learned back into the knowledge base. Every stage is persisted, auditable, and resumable.

---

## 0. Where the solution stands today

| Area | Current state | Impact on this plan |
|---|---|---|
| `IAnthropicService` / `AnthropicService` | Text in, text out. No tool use, no structured output, no usage capture. | Replaced by a richer `IReasoningEngine`; the old interface stays for the chat UI. |
| SDK reference | `Anthropic.SDK` 5.10.0 (community package) but `using Anthropic` + `Anthropic.AnthropicClient` (official namespace). The file cannot compile as both at once. | Phase 0 moves to the official `Anthropic` NuGet package. |
| Model IDs | `claude-sonnet-4-20250514`, `claude-haiku-4-20250514` (retired). | Phase 0 moves to `claude-opus-5-5` (default) and `claude-haiku-5-5` (cheap routes). |
| Retry | Polly retry on `HttpRequestException` only. | The official SDK retries 408/409/429/5xx itself; Polly is removed from the AI path. |
| Entities | `AIConversation`, `AIMessage`, `AIEscalation`, `AIKnowledgeEntry`, `AIAnalyticsEvent`. | Reused as episodic memory, the HITL escalation path, and the Learn stage sink. |
| Rate limiting | Built-in ASP.NET Core limiter, `chat` policy + global per-user limiter. | Reused by the Control Plane for the HTTP edge; a second limiter is added for tool dispatch. |
| Logging | Serilog referenced. | Evidence Plane adds OpenTelemetry on top. |
| Tests | Empty xUnit placeholders. | Each phase ships with its own tests. |

---

## 1. Plane-to-project mapping

Dependency flow stays **Web/Admin → Infrastructure → Core**. Core gets a new `Agents/` folder holding the
contracts and entities for all five planes; Infrastructure holds the implementations; Web hosts the run worker and
learner-facing endpoints; Admin hosts the approval queue and the trace viewer.

```
Intelligently.Core/
  Agents/
    Contracts/        IReasoningEngine, IContextAssembler, IToolDispatcher, IPolicyDecisionPoint, IEvidenceSink
    Entities/         AgentRun, AgentStep, AgentPlan, AgentApproval, AgentTrace, AgentEpisode, IdempotencyRecord
    StateGraph/       AgentStage (enum), StageTransition, RecoveryBudget, IStageHandler
    Tools/            ToolDescriptor, ToolCall, ToolResult, ToolRiskLevel
Intelligently.Infrastructure/
  Agents/
    Intelligence/     AnthropicReasoningEngine, CognitiveRouter, CandidateSearch
    Context/          ContextAssembler, TokenBudgeter, EpisodicMemoryStore, KnowledgeRetriever, Compactor
    Execution/        ToolRegistry, McpToolSource, DockerSandbox, WasmSandbox, RpcDispatcher
    Control/          PolicyDecisionPoint, IdempotencyBarrier, ToolRateLimiter, ApprovalGate
    Evidence/         OtelEvidenceSink, CostAccountant, TraceWriter
    StateGraph/       StateGraphRunner, stage handlers (Interpret…Learn), RunQueue (Channel<T>)
Intelligently.Web/      /agent/runs endpoints, htmx run status partial, RunWorker (BackgroundService)
Intelligently.Admin/    /approvals queue, /runs/{id} trace viewer, policy editor
```

### 1.1 Intelligence Plane

**Responsibility:** turn intent into structured candidate tool calls and plans.

- `IReasoningEngine` exposes three operations: `PlanAsync` (structured output → `AgentPlan`), `DecideAsync`
  (tool-use turn → zero or more `ToolCall`s), `JudgeAsync` (score a candidate against a rubric).
- `AnthropicReasoningEngine` uses the official SDK's `client.Messages.Create` with:
  - `Thinking = new ThinkingConfigAdaptive()` and `OutputConfig.Effort` set per task class.
  - `OutputConfig.Format = JsonOutputFormat` for Plan/Judge so the response is schema-valid JSON.
  - `Strict = true` on every tool definition so tool inputs always validate.
  - `fallbacks: "default"` with the `server-side-fallback-2026-07-01` beta so a safety refusal routes to a fallback
    model instead of failing the run. Always check `StopReason` before reading content.
  - Streaming for any call with large `MaxTokens` (plan generation, long summaries).
- `CognitiveRouter` picks model + effort from the task class:

  | Task class | Model | Effort | Why |
  |---|---|---|---|
  | Interpret, Plan, Verify judgement | `claude-opus-5-5` | `high` | Correctness of the plan decides everything downstream. |
  | Act (tool selection within a plan step) | `claude-opus-5-5` | `medium` | Scoped decision; the plan carries most of the reasoning. |
  | Classification, triage, routing, summaries | `claude-haiku-5-5` | `low` | High volume, low stakes. |

- `CandidateSearch` (test-time compute) is optional and phase-gated: generate *N* plans in parallel, score each with
  `JudgeAsync`, keep the best. Enabled per task type by configuration so cost stays explicit.

**Schema contracts in .NET.** The paper's "Pydantic contracts" become C# records with JSON Schema generated by
`System.Text.Json.Schema.JsonSchemaExporter`. The same record defines the tool `InputSchema`, the structured-output
format, and the deserialization target, so there is one source of truth per contract.

### 1.2 Context Plane

**Responsibility:** assemble the prompt within budget, keep the cache warm, remember across runs.

- `IContextAssembler.BuildAsync(run, stage)` returns a `PromptEnvelope` (system blocks, tools, messages) built in
  the fixed order **stable → volatile** so the prefix cache holds:
  1. Frozen agent system prompt (cache breakpoint).
  2. Deterministic, sorted tool list (cache breakpoint).
  3. Retrieved knowledge entries for the course/module.
  4. Episodic summary of prior runs for this learner and task type.
  5. The run transcript (append-only).
  6. Volatile per-stage instruction as a mid-conversation `system` message in `messages[]`, never in the top-level
     system field, so the cached prefix survives.
- `TokenBudgeter` calls `client.Messages.CountTokens` before each model call and trims from the bottom of the
  priority list (episodic summary first, then knowledge entries) until the envelope fits the stage budget.
- `EpisodicMemoryStore` reads/writes `AgentEpisode` rows: one per completed run with outcome, a short summary,
  tool usage counts, and the embedding of the task text for similarity lookup.
- `KnowledgeRetriever` queries `AIKnowledgeEntry` (existing). Phase 1 uses SQL full-text search; Phase 3 adds an
  embedding column and vector search. The "knowledge graph" is modelled as tagged entries plus
  `AgentEpisode → AIKnowledgeEntry` links, which is enough for retrieval without a graph database.
- `Compactor` has two modes: server-side compaction (`compact-2026-01-12` beta, append `response.content` back on
  every turn so compaction blocks survive) for long runs, and a local Haiku summary for storing episodes.
- Cache health is observable: `CacheReadInputTokens == 0` on a repeated prefix raises a warning metric.

### 1.3 Execution Plane

**Responsibility:** the only place side effects happen.

- `ToolRegistry` holds `ToolDescriptor`s: name, description, JSON schema, `ToolRiskLevel` (ReadOnly, Mutating,
  Destructive, External), idempotency mode, and the `IToolHandler` that runs it.
- Tool sources:
  - **Native tools** (C#): knowledge lookup, course/module lookup, escalation creation, learner notification.
  - **MCP tools**: `McpToolSource` uses the official `ModelContextProtocol` C# SDK to connect to configured servers
    (stdio or HTTP), list their tools, and register them with a namespace prefix. Risk level defaults to
    `External` until an operator overrides it in configuration.
  - **Sandboxed code execution**: `DockerSandbox` (via `Docker.DotNet`) runs learner lab code in a throwaway container
    with no network, a CPU/memory limit, and a wall-clock timeout. `WasmSandbox` (via `Wasmtime`) covers short,
    deterministic snippets with sub-second startup. Firecracker is noted as a production option on Linux hosts only
    and is not part of the Windows developer path.
- `IToolDispatcher.DispatchAsync(call)` is the single entry point. It never runs a tool directly; it asks the
  Control Plane first (section 1.4), then executes, then hands the `ToolResult` to the Evidence Plane.
- Parallel tool calls from one assistant turn execute concurrently and return **all** results in one user message.
  A failed tool returns `is_error = true` rather than being dropped.
- Every `ToolResult` is size-capped and normalized (stdout, stderr, exit code, structured payload) so the Verify stage
  has a consistent shape to validate.

### 1.4 Control Plane

**Responsibility:** decide whether a proposed mutation may proceed.

- `IPolicyDecisionPoint.EvaluateAsync(ToolCall, RunPrincipal)` returns `Allow`, `Deny(reason)`, or
  `RequireApproval(reason)`. Rules are data (JSON in configuration, editable from Admin), evaluated in order:
  1. **Identity**: the run carries a `RunPrincipal` (user id, roles, course scope). A tool with a course scope must
     match the principal's enrolment. No principal, no tool.
  2. **Risk gate**: `ReadOnly` → Allow. `Mutating` → Allow if inside the run's declared scope, else RequireApproval.
     `Destructive` and `External` → RequireApproval always.
  3. **Rate limits**: a per-run and per-user `PartitionedRateLimiter` for tool dispatch, separate from the HTTP
     limiter already in `Program.cs`.
  4. **Idempotency barrier**: `IdempotencyRecord` keyed on `(RunId, ToolName, SHA-256(args))`. A replayed call
     returns the stored result instead of re-executing. Required for every `Mutating` tool so Recover can retry a
     step safely.
- `ApprovalGate` parks the run in `AwaitingApproval`, writes an `AgentApproval` row, and notifies instructors
  through the existing escalation path. Admin shows the pending call, its arguments, and the trace so far; approve
  or deny resumes the run with the decision recorded as evidence.
- Deny is final for that call; the state graph moves to **Recover**, which must replan without that tool.

### 1.5 Evidence Plane

**Responsibility:** make every run reconstructible after the fact.

- OpenTelemetry (`OpenTelemetry.Extensions.Hosting`, OTLP exporter, console exporter in Development). One root span
  per run, one child span per stage, one grandchild per model call or tool call. Attributes carry model id, effort,
  input/output/cache tokens, cost, stop reason, tool name, risk level, PDP decision, sandbox exit code, latency.
- `AgentTrace` table is **append-only**: the exact prompt envelope sent, the raw response content, tool payloads,
  sandbox logs, and the provider's reasoning summary (`Thinking.Display = Summarized`, stored as text). Rows are
  never updated; corrections are new rows.
- `CostAccountant` converts usage into dollars from a configured price table and adds it to `AgentRun.TotalCostUsd`
  and to `AIAnalyticsEvent` for existing reporting.
- `AgentRun` and `AgentStep` give the operational view (stage, status, retries left, budgets left).
- Evaluation harness (tests project): replays stored traces against a rubric so prompt or policy changes can be
  measured, not guessed.

---

## 2. The 7-Stage Conditional State Graph

### 2.1 Stages and persisted state

```csharp
public enum AgentStage { Interpret, Plan, Act, Observe, Verify, Recover, Learn, AwaitingApproval, Completed, Failed }

public sealed class AgentRun
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public int CourseId { get; set; }
    public string TaskType { get; set; } = "";        // e.g. LabErrorDiagnosis
    public string TaskSpec { get; set; } = "";        // JSON, validated at Interpret
    public AgentStage Stage { get; set; }
    public string Status { get; set; } = "Running";   // Running | Waiting | Completed | Failed | Cancelled
    public int PlanRevision { get; set; }
    public RecoveryBudget Budget { get; set; } = new();   // owned entity, see 2.3
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public decimal TotalCostUsd { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public ICollection<AgentStep> Steps { get; set; } = [];
    public ICollection<AgentTrace> Traces { get; set; } = [];
}
```

Each stage handler implements `IStageHandler` and returns a `StageOutcome { Next, Reason }`. The runner persists the
run after every transition, so a crash or restart resumes from the last committed stage.

### 2.2 Transition table

| From | Outcome | To |
|---|---|---|
| Interpret | spec valid, within budget | Plan |
| Interpret | spec invalid or out of scope | Failed (with reason) |
| Plan | plan produced and schema-valid | Act |
| Plan | planner cannot produce a plan | Recover |
| Act | tool calls dispatched | Observe |
| Act | PDP returned RequireApproval | AwaitingApproval |
| AwaitingApproval | approved | Act (resumes same step) |
| AwaitingApproval | denied or timed out | Recover |
| Observe | results captured | Verify |
| Verify | step passes, plan has more steps | Act |
| Verify | step passes, plan complete | Learn |
| Verify | step fails | Recover |
| Recover | budget allows replan | Plan (PlanRevision + 1) |
| Recover | budget allows retry of same step | Act |
| Recover | budget exhausted or oscillation detected | escalate → `AIEscalation` created → Failed |
| Learn | episode and traces written | Completed |

Stage handlers are the only code allowed to return a next stage, and the runner rejects any transition not in this
table, so the graph cannot drift into an unbounded loop.

### 2.3 Monotonic recovery edges

`RecoveryBudget` is a set of counters that only decrease: `ReplansLeft` (default 3), `RetriesLeft` per step
(default 2), `ToolCallsLeft` (default 40), `TokensLeft` (default 300k), `WallClockLeft`. Every recovery edge spends at
least one counter, so the run is guaranteed to terminate.

Oscillation damping: Recover hashes the new plan; if the hash matches any of the last three plan revisions, the edge
is treated as exhausted and the run escalates. The same applies to a step that fails twice with the same verifier
message.

### 2.4 Verify stage contracts

Verify evaluates `ToolResult`s against, in order:

1. **Schema contract**: the tool's declared output schema (JSON Schema validated with `JsonSchema.Net`).
2. **Deterministic assertions**: exit code, compiler/linter output, test runner summary parsed from sandbox output.
3. **Ground-truth gates**: for tasks with a known answer (graded labs), compare against the stored expected output.
4. **Model judgement**: only when 1–3 cannot decide, `JudgeAsync` with a rubric; the score and rationale are stored as
   evidence.

### 2.5 Execution host

Runs are queued on a bounded `Channel<Guid>` and processed by `RunWorker : BackgroundService` in the Web project.
The worker loads the run, calls `StateGraphRunner.StepAsync` until the run is `Waiting`, `Completed`, or `Failed`,
and commits after every stage. Waiting runs re-enter the queue when an approval is recorded. A second worker instance
is safe because each run row is claimed with an optimistic concurrency token.

---

## 3. Phased delivery

Each phase ends with passing tests and a demoable slice. Estimates assume one developer.

### Phase 0 — Foundation (≈2 days)

- Replace `Anthropic.SDK` with the official `Anthropic` package; rewrite `AnthropicService` against
  `client.Messages.Create` / `.Stream`, current model IDs, adaptive thinking, and `StopReason` handling.
- Drop Polly from the AI path (the SDK retries); keep `Polly.Extensions.Http` only if used elsewhere.
- Add OpenTelemetry tracing + metrics with OTLP and console exporters; wire Serilog to the OTel trace id.
- Add `Agents` configuration section (`Agents:DefaultModel`, `Agents:Budgets:*`, `Agents:Sandbox:*`).
- Tests: `AnthropicService` behaviour with a fake HTTP handler.

### Phase 1 — Core contracts and walking skeleton (≈4 days)

- Core: entities, enums, `IStageHandler`, `IReasoningEngine`, `IToolDispatcher`, `IPolicyDecisionPoint`,
  `IEvidenceSink`, `IContextAssembler`.
- Infrastructure: `StateGraphRunner` with the transition table, EF configuration and migration for the agent tables,
  `RunQueue`, in-memory `ToolRegistry` with one `ReadOnly` native tool (`lookup_knowledge`).
- Web: `POST /agent/runs`, `GET /agent/runs/{id}` (htmx polling partial), `RunWorker`.
- Reasoning engine is real (Opus 5.5) but only Interpret → Plan → Act → Observe → Verify → Learn with the single
  tool; Recover exists but only escalates.
- Tests: every transition in the table, budget decrement, resume after simulated crash, rejected illegal transition.

### Phase 2 — Control Plane (≈4 days)

- `PolicyDecisionPoint` with JSON rules, `IdempotencyBarrier`, tool-dispatch rate limiter, `ApprovalGate`.
- Admin: approvals queue page, approve/deny with htmx, run trace viewer (read-only).
- Recover gains replan and retry edges plus oscillation detection.
- Tests: PDP rule ordering, idempotent replay returns stored result, approval resume, deny → Recover → replan,
  oscillation triggers escalation.

### Phase 3 — Context Plane (≈4 days)

- `ContextAssembler` with stable-prefix ordering and cache breakpoints; `TokenBudgeter` with `CountTokens`;
  `EpisodicMemoryStore`; `KnowledgeRetriever` (full-text, then embeddings); `Compactor`.
- Metric: cache hit ratio per run; alert when zero on a repeated prefix.
- Tests: assembler produces byte-identical prefixes across turns; budgeter trims in priority order; compaction
  blocks are preserved across turns.

### Phase 4 — Execution Plane (≈5 days)

- `McpToolSource` with the official MCP C# SDK; configuration for servers; namespaced registration.
- `DockerSandbox` (no network, limits, timeout, output capture) and `WasmSandbox`.
- Native `run_lab_code` tool backed by the Docker sandbox; `Mutating` tools `create_escalation`,
  `notify_learner`.
- Tests: sandbox timeout and memory kill paths, MCP tool listing against a stub server, parallel tool results
  returned in one message, `is_error` propagation.

### Phase 5 — Evidence Plane and Learn (≈3 days)

- `OtelEvidenceSink` spans and attributes as in 1.5; `TraceWriter` append-only rows; `CostAccountant`.
- Learn stage: write `AgentEpisode`, link to `AIKnowledgeEntry` when an instructor marks the outcome reusable
  (reuses the existing `AddToKnowledgeBase` flow on `AIEscalation`).
- Admin trace viewer shows prompt, response, tool payloads, sandbox logs, reasoning summary, cost per step.
- Evaluation harness: replay stored traces against rubrics; a CI job fails if the pass rate drops below a threshold.

### Phase 6 — Pilot task: lab error diagnosis (≈3 days)

- Task spec schema: course, module, learner code, error text, expected output (optional).
- Plan template: reproduce in sandbox → diagnose → propose fix → re-run → explain to learner at their level.
- Ground-truth gate when expected output exists; model judgement otherwise.
- Escalation when the fix touches course material the agent is not allowed to change.
- Optional `CandidateSearch` (N=3 plans) enabled for this task type only, measured against the harness.

---

## 4. Cross-cutting decisions

- **One model family, one cache namespace.** Opus 5.5 for reasoning stages, Haiku 5.5 only for routes that are
  clearly classification or summarization. Measure effort before adding a cascade.
- **Append-only everywhere the model sees history.** Thinking blocks are bound to the model and conversation; editing
  earlier turns invalidates them. Runs never rewrite history; compaction uses the server-side feature.
- **No forced tool choice.** Opus 5.5 rejects `tool_choice: any/tool`. Use `auto` plus an instruction, strict tools for
  arguments, and structured outputs where the call only existed to get JSON.
- **Contracts are C# types.** One record per tool input, tool output, plan, and task spec; schema exported from the
  type; the same type deserializes the result.
- **Side effects only in the Execution Plane, decisions only in the Control Plane.** No stage handler calls a tool
  directly or decides its own permission.
- **Windows developer path.** Docker Desktop (WSL2) and Wasmtime work locally; Firecracker is a Linux production
  option documented but not implemented.

## 5. Open questions to settle before Phase 1

1. Which task types come after lab error diagnosis (grading, quiz generation, curriculum gap analysis)? The task
   spec schema should be designed with the second one in mind.
2. Who approves `Mutating` calls outside office hours? A timeout policy (deny after N hours vs. hold) changes the
   Recover design.
3. Is a vector store acceptable in SQL Server, or should embeddings go to a dedicated service from the start?
4. Cost ceiling per run and per learner per day. These become `RecoveryBudget` and PDP defaults.

## 6. Packages to add

| Package | Project | Purpose |
|---|---|---|
| `Anthropic` | Infrastructure | Official Claude SDK (replaces `Anthropic.SDK`). |
| `ModelContextProtocol` | Infrastructure | MCP client for external tool servers. |
| `Docker.DotNet` | Infrastructure | Container sandbox. |
| `Wasmtime` | Infrastructure | WebAssembly sandbox. |
| `JsonSchema.Net` | Infrastructure | Validate tool outputs against declared schemas. |
| `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Exporter.Console` | Web, Admin | Evidence Plane telemetry. |
| `Serilog.Enrichers.Span` or equivalent | Web, Admin | Correlate logs with traces. |
