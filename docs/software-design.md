# Software Design

This document describes how the autonomous agent runtime in Intelligently is built: the contracts, the
classes behind each plane, the state graph runner and its handlers, the tool dispatch pipeline, the
policy engine, dependency injection, and the extension points. It describes the code as it is, and
section 11 lists where the code departs from the original plan.

Related documents: [Architecture](architecture.md), [Database Design](database-design.md),
[User Guide](user-guide.md).

---

## 1. Layering and folder layout

Dependencies flow one way: Web and Admin reference Infrastructure, Infrastructure references Core, and
Core references nothing outside the .NET base class library.

```
src/Intelligently.Core/Agents
  Contracts/     plane interfaces and their records (PromptEnvelope, PolicyDecision, ...)
  Entities/      AgentRun, AgentStep, AgentPlan, AgentTrace, AgentEpisode, AgentApproval, IdempotencyRecord
  StateGraph/    AgentStage, StageOutcome, StageTransitions, RecoveryBudget, IStageHandler
  Tasks/         LabErrorDiagnosisSpec and its phases
  Tools/         ToolCall, ToolResult, ToolDescriptor, ToolRiskLevel, IToolHandler, IToolRegistry, CanonicalJson
  AgentsOptions.cs

src/Intelligently.Infrastructure/Agents
  StateGraph/    StateGraphRunner, RunQueue, StepResult, Handlers/ (one per stage)
  Intelligence/  AnthropicReasoningEngine, PromptEnvelopeMapper, AgentJsonSchema, PlanValidator, CandidateSearch
  Context/       ContextAssembler, TokenBudgeter, token counters, EpisodicMemoryStore, KnowledgeRetriever, Compactor, CacheHealthMonitor
  Execution/     ToolRegistry, ToolDispatcher, ToolOutputValidator, native tools, Mcp/, Sandboxes/
  Control/       PolicyDecisionPoint, IdempotencyBarrier, ToolRateLimiter, ApprovalGate
  Evidence/      OtelEvidenceSink, TraceWriter, CostAccountant
  Learn/         EpisodeWriter, EscalationResolver
  Tasks/         TaskTypeRegistry, LabErrorDiagnosisContract, UnifiedDiff
  AgentRunService.cs, ProcessWorkerIdentity.cs, AgentServiceCollectionExtensions.cs

src/Intelligently.Web/Agents        RunWorker, AgentEndpoints
src/Intelligently.Web/Pages/Agent   demo form, run page, _RunStatus partial
src/Intelligently.Admin/Pages       Approvals, Runs, Escalations
```

Core holds only interfaces, records, entities, and options so that every plane can be replaced or
tested without the others.

---

## 2. Core contracts

### 2.1 Plane interfaces

| Interface | Plane | Members |
|---|---|---|
| `IReasoningEngine` | Intelligence | `PlanAsync(envelope)` returns a `PlanResult`; `DecideAsync(envelope)` returns a `DecisionResult` with tool calls; `JudgeAsync(envelope, rubric)` returns a `JudgementResult` |
| `ICandidateSearch` | Intelligence | `IsEnabledFor(taskType)`; `SearchAsync(run, envelope)` returns the best of N plans |
| `IContextAssembler` | Context | `BuildAsync(run, stage)` returns a `PromptEnvelope` |
| `IPromptTokenCounter` | Context | `CountAsync(envelope)` |
| `IKnowledgeRetriever` | Context | `SearchAsync(query, courseId, moduleId, maxResults)` |
| `IEpisodicMemoryStore` | Context | `WriteAsync(run, outcome, summary, toolUsage)`; `RecallAsync(userId, courseId, taskType, taskText, limit)` |
| `IEpisodeSummarizer` | Context | `SummarizeAsync(run, transcript)` |
| `IToolDispatcher` | Execution | `DispatchAsync(run, calls, stepId)` returns a `DispatchResult` |
| `IToolHandler`, `IToolRegistry`, `IToolSource` | Execution | A handler owns a `ToolDescriptor` and executes calls; the registry resolves handlers by name; a source contributes handlers dynamically |
| `ISandbox` | Execution | `Name`; `RunAsync(request)` returns a `SandboxResult` |
| `IPolicyDecisionPoint` | Control | `EvaluateAsync(request)` returns a `PolicyDecision` |
| `IToolRateLimiter` | Control | `TryAcquireAsync(request)` |
| `IIdempotencyBarrier` | Control | `ComputeKey(call)`; `FindAsync(runId, tool, key)`; `RecordAsync(runId, call, key, result)` |
| `IApprovalGate` | Control | `RequestAsync`, `FindApprovedAsync`, `FindLatestAsync`, `ListPendingAsync`, `DecideAsync` |
| `IEvidenceSink` | Evidence | `StartRun`, `StartStage`, `StartModelCall`, `StartToolCall` return spans; `RecordAsync(run, trace)` appends a row; `FinishRunAsync(run)` writes the analytics event |
| `ICostAccountant` | Evidence | `CostOf(usage)`; `Charge(run, usage)` |
| `IEscalationResolver` | Learn | `ResolveAsync(escalationId, instructor, response, addToKnowledgeBase)` |
| `ITaskContract`, `ITaskTypeRegistry` | Cross-cutting | A contract per task type: spec schema and validation, plan template, plan validation, step verification, judgement rubric, protected-change detection, learner output |
| `IStageHandler` | State graph | `Stage`; `ExecuteAsync(run)` returns a `StageOutcome` |
| `IRunQueue`, `IWorkerIdentity` | Hosting | Bounded queue of run ids; the worker's identity string |

### 2.2 Key records

**Prompt envelope.** `PromptEnvelope(System, Tools, Messages)` is the unit of work the Intelligence
Plane receives. System blocks and messages carry a `CacheBreakpoint` flag. Each message has a role
(`user`, `assistant`, or `system`), a list of content blocks (`text`, `tool_use`, `tool_result`, or
`raw` provider blocks), and a `PromptSection` (`Knowledge`, `Episodic`, `Transcript`, `Instruction`)
that the budgeter uses when trimming. The envelope computes a deterministic prefix hash over its system
blocks and tools so cache behaviour can be observed.

**Reasoning results.** `PlanResult` carries an `AgentPlan` or a failure reason, plus usage, the raw
response, the content blocks, and call metadata. `DecisionResult` carries tool calls, assistant text,
the stop reason, and the same extras. `JudgementResult` carries pass or fail, a score in 0 to 1, and a
rationale. `ModelUsage` holds the four token classes. `ModelCallMetadata` holds the reasoning summary,
stop reason, effort, and latency.

**Tools.** `ToolCall(Id, Name, Input)`. `ToolResult(ToolCallId, Name, IsError, Content, ErrorKind,
Payload)` with error kinds `ToolFailure`, `PolicyDenied`, `RateLimited`, `BudgetExhausted`, and
`OutputInvalid`. `ToolDescriptor(Name, Description, InputSchema, RiskLevel, CourseScopeArgument,
OutputSchema, Strict, Exclusive)`: the course-scope argument names the input field the policy engine
checks against enrolment, and `Exclusive` marks handlers that write through the scoped DbContext and must
not run concurrently. `ToolRiskLevel` is `ReadOnly`, `Mutating`, `Destructive`, or `External`.

**Policy.** `PolicyRequest(Run, Call, Descriptor, Principal)` in; `PolicyDecision(Verdict, Reason,
RuleKind, Rule, ReplayedResult, IdempotencyKey)` out, with verdicts `Allow`, `Deny`, and
`RequireApproval`. `PolicyRule` is the configuration shape (section 7). `RunPrincipal(UserId, Roles,
CourseScope)` is the identity every decision is made against.

**Sandbox.** `SandboxRequest(Command, Files)` with optional timeout, memory, CPU, output cap, and
environment. `SandboxResult(Sandbox, ExitCode, Stdout, Stderr, TimedOut, MemoryExceeded, Duration,
OutputTruncated, Error)`.

**Task contracts.** `TaskSpecValidation`, `PlanTemplate` with `PlanTemplateStep(Phase, Description,
Tool, SuccessCriteria)`, `StepVerification` (the run, plan, plan step, step, calls, results, and prior
steps), `StepVerdict`, `JudgementSpec(Rubric, MinimumScore)`, and `LearnerOutput(Explanation, FixDiff)`.

**Canonical JSON.** `CanonicalJson.Serialize` sorts object keys at every level and removes whitespace;
`CanonicalJson.Sha256` hashes the result. Idempotency keys and approval matching both use it, so the
same arguments in a different order produce the same key.

### 2.3 Schema export

`AgentJsonSchema` turns C# records into JSON Schema with `System.Text.Json.Schema.JsonSchemaExporter`.
It removes the `$schema` keyword, copies `[Description]` attributes onto properties, closes every object
with `additionalProperties: false`, and marks properties required: all of them for tool contracts and
structured outputs, only the non-nullable ones for task specs. One record therefore serves as the tool
input schema, the structured-output format, and the deserialization target.

---

## 3. The state graph

### 3.1 Stages and transitions

`AgentStage` has ten values: the seven working stages (`Interpret`, `Plan`, `Act`, `Observe`, `Verify`,
`Recover`, `Learn`), the parked stage `AwaitingApproval`, and the terminal stages `Completed` and
`Failed`. `StageTransitions.Allowed` is the only source of legal edges:

| From | To | When |
|---|---|---|
| Interpret | Plan | Spec valid, within budget |
| Interpret | Failed | Spec invalid or out of scope |
| Plan | Act | Plan produced and valid |
| Plan | Recover | Planner could not produce a plan |
| Act | Observe | Tool calls dispatched, or the model answered in text |
| Act | AwaitingApproval | The policy engine required approval |
| AwaitingApproval | Act | Approved; the same step resumes |
| AwaitingApproval | Recover | Denied |
| Observe | Verify | Results captured |
| Verify | Act | Step passed, more steps remain |
| Verify | Learn | Step passed, plan complete |
| Verify | Recover | Step failed |
| Recover | Plan | A replan was affordable |
| Recover | Act | A retry was affordable |
| Recover | Failed | Budget exhausted or oscillation; an escalation is raised first |
| Learn | Completed | Episode and learner output written |

The runner rejects any other edge a handler returns and fails the run. The runner's own failure path,
used when a handler throws, sets the stage to `Failed` directly.

### 3.2 Recovery budget

`RecoveryBudget` is an owned type on the run with five counters that only decrease:

| Counter | Default | Spent by |
|---|---|---|
| `ReplansLeft` | 3 | Recover, on the edge to Plan |
| `RetriesLeft` | 2 | Recover, on the edge to Act; one counter for the whole run |
| `ToolCallsLeft` | 40 | Act, once per proposed tool call before dispatch |
| `TokensLeft` | 300,000 | The cost accountant, with every token class of every charged model call |
| `WallClockLeft` | 30 min | The runner, with the time spent inside each stage |

`IsExhausted` is true when tokens, tool calls, or wall clock reach zero. The handlers check it before
and after expensive work, and Recover treats it as a reason to escalate rather than replan. Time spent
in the queue or waiting for an approval is not charged.

### 3.3 StateGraphRunner

`StateGraphRunner` executes exactly one stage per call and is the only code that moves a run between
stages. `TryStepAsync(runId, claimedBy)`:

1. **Claim.** One auto-committed conditional `UPDATE` sets `ClaimedBy` and `ClaimedUntil` on the run,
   matching only when the status is `Running` and no live lease exists. Zero rows means another worker
   holds the run; the call returns without executing.
2. **Open.** Loads the run tracked, resolves the handler for its stage, starts the stage span and a
   stopwatch, and opens a database transaction.
3. **Execute.** Calls the handler. Any exception other than cancellation fails the run with the error in
   its status reason, records a note trace, finishes the run's evidence, commits, and rethrows.
4. **Validate.** Checks the returned edge against the transition table; an illegal edge fails the run the
   same way.
5. **Apply.** Spends wall clock, sets the new stage and status reason, derives the status (`Completed`,
   `Failed`, `Waiting` for `AwaitingApproval`, else `Running`), and sets `CompletedAt` on terminal stages.
6. **Record.** Appends a `Transition` trace with the elapsed time, tags the span, and records the stage
   duration histogram. A terminal stage also writes the run-finished analytics event.
7. **Commit.** Regenerates `ConcurrencyStamp`, clears both lease columns, saves, and commits the
   transaction. Both `ConcurrencyStamp` and `ClaimedUntil` are concurrency tokens, so a worker whose
   lease was taken over fails here instead of overwriting the newer state.
8. **Release.** If nothing was committed, a best-effort conditional update clears the lease only when it
   is still this worker's.

Handlers mutate the tracked run in memory and return `StageOutcome.To(next, reason)`. Everything a
handler or an exclusive tool saves mid-stage lands inside the open transaction, so an escalation or an
idempotency receipt never becomes visible without its transition.

### 3.4 Stage handlers

**Interpret** makes no model call. It fails the run on an empty task type, a missing or unauthenticated
principal, a spec that is not a non-empty JSON object, a spec the task contract rejects, or an already
exhausted budget. Otherwise it moves to Plan.

**Plan** builds the envelope and either runs candidate search (when enabled for the task type) or makes
one `PlanAsync` call at high effort. It charges the usage, checks the budget, validates the plan with
`PlanValidator` (goal present, steps indexed 0..n, descriptions present, tool names known, then the
contract's own rules), persists an `AgentPlan` row with a SHA-256 hash of its goal and steps, resets the
step index, and moves to Act. Any failure moves to Recover with reason `PlanFailed`.

**Act** first looks for a parked step at the current revision and index. If one exists, it re-dispatches
only the calls that have no result yet, with no new model turn. Otherwise it creates a new `AgentStep`
with the next attempt number, builds the envelope, calls `DecideAsync` at medium effort, stores the
assistant text, tool calls, and response blocks on the step, spends one tool call of budget per
proposed call, and dispatches. A `RequiresApproval` result parks the step, requests the approval, and
moves to AwaitingApproval. Otherwise the results are stored and the run moves to Observe.

**AwaitingApproval** reads the run's latest approval. Approved moves to Act, where the parked step
resumes. Denied marks the step failed and moves to Recover with reason `MaterialDenied` when the
course-material rule fired, or `PolicyDenied` otherwise.

**Observe** truncates each result's content to the configured limit, marks the step `Observed`, records
a note trace with the results and errors, and moves to Verify.

**Verify** runs three layers in order. The generic checks fail a step whose required tool was not
called or whose results contain an error. The task contract's `VerifyStep` then returns pass, fail, or
null. On null, if the contract supplies a `JudgementRubric`, the handler calls `JudgeAsync` at high
effort, records a `Judgement` trace, and enforces the minimum score. A pass marks the step `Passed` and
moves to Act for the next step or to Learn when the plan is complete; a fail marks it `Failed` and moves
to Recover with reason `StepFailed`, or `PolicyDenied` when the failing result was a policy denial.

**Recover** decides in a fixed order: an exhausted budget escalates; oscillation escalates (a plan hash
repeated within the last three revisions, or the same verifier message on the last two failed attempts
of a step); `MaterialDenied` escalates; `StepFailed` with retries left moves to Act; a replan with
replans left increments the plan revision and moves to Plan; anything else escalates. Escalation
inserts a high-priority `AIEscalation`, records an `Escalation` trace, and moves to Failed.

**Learn** asks the task contract to compose the learner output, stores the explanation and fix diff on
the run, writes the episode through `EpisodeWriter`, and moves to Completed.

---

## 4. Intelligence Plane

### 4.1 AnthropicReasoningEngine

A singleton over the official Anthropic C# SDK, calling the beta Messages endpoint. Every request uses
the configured default model, a 16,000 token output cap, adaptive thinking with summarized display, the
system blocks and tools mapped from the envelope, and server-side compaction when the envelope asks for
it.

| Operation | Effort | Output constraint | Result |
|---|---|---|---|
| `PlanAsync` | high | Structured output against the `AgentPlan` schema | The plan, or a failure reason on a `max_tokens` stop or invalid JSON |
| `DecideAsync` | medium | Tools with `tool_choice: auto`; strict schemas for native tools | Text plus zero or more tool calls |
| `JudgeAsync` | high | Structured output against a `{passed, score, rationale}` schema | The judgement |

The call loop records the prefix hash with the cache health monitor, sums usage across continuation
calls, concatenates the summarized thinking blocks into the reasoning summary, replays the response
blocks and loops when the stop reason is `compaction`, and throws `AIRefusalException` on a `refusal`
stop. The result carries every content block as JSON so later turns can replay the provider's output
unchanged.

`PromptEnvelopeMapper` converts envelope blocks to SDK types: system blocks with a cache control where
flagged, tools with a cache control on the last one, `system` messages as system-role entries inside
the message list, and raw blocks passed through verbatim.

### 4.2 CandidateSearch

When enabled for a task type, Plan generates N candidates in parallel from the same envelope, each in
its own model-call span. Every candidate is validated with `PlanValidator`. The valid ones are then
judged in parallel against the contract's plan rubric (or a generic one that passes at 0.6), using an
envelope with the instruction removed, the candidate appended as an assistant turn, and a system message
asking for a judgement only. The highest-scoring valid candidate wins, earliest on ties. Every planner
and judge call is charged to the run, and one `Candidate` trace is written per candidate with its score,
rationale, steps, and cost.

### 4.3 Model and effort routing

Model and effort are fixed per operation rather than chosen by a router:

| Call | Model setting | Effort |
|---|---|---|
| Plan, candidate planners | `Agents:DefaultModel` | high |
| Act | `Agents:DefaultModel` | medium |
| Verify judge, candidate judges | `Agents:DefaultModel` | high |
| Episode summary | `Agents:CheapModel` | low |
| Token counting | `Agents:DefaultModel` | not applicable |

---

## 5. Context Plane

### 5.1 ContextAssembler

`BuildAsync(run, stage)` produces the envelope in a fixed order, stable content first so the prompt
cache prefix holds across turns:

1. **System.** One constant block with a cache breakpoint.
2. **Tools.** Every registered descriptor, sorted by name, with a cache breakpoint on the last.
3. **Knowledge** (user message, omitted when empty). Entries from `IKnowledgeRetriever` for the run's
   course and module, queried with the task text.
4. **Episodic** (user message, omitted when empty). Episodes from `IEpisodicMemoryStore`, one line each
   with date, outcome, whether it was this learner, summary, and tools used.
5. **Transcript.** The task message, then for every plan revision: the plan replayed from its stored
   response blocks, a marker message, and each finished step as an assistant turn (replayed blocks) and
   a user turn with one tool result per call plus the verifier message. A cache breakpoint goes on the
   last transcript block.
6. **Instruction** (system-role message inside the message list, never the top-level system field).
   Plan: produce the plan as JSON, the tool list, step guidance, the task template, and a note about the
   previous failure on a replan. Act: execute step i with its description, success criteria, phase, and
   tool. Any other stage: judge the most recent step.

The envelope then passes through the budgeter, and `Compaction` is set when the counted tokens reach
`Agents:Context:CompactAfterTokens`.

### 5.2 TokenBudgeter and counters

The budgeter counts the envelope with `IPromptTokenCounter` and, while it exceeds the stage's prompt
budget, trims the Episodic section first and the Knowledge section second, one entry block at a time,
removing the whole message once only the header remains. It never trims the system prompt, tools,
transcript, or instruction. `AnthropicTokenCounter` calls the SDK's count-tokens endpoint with the same
mapping the engine uses and falls back to `EstimatingTokenCounter` (about four characters per token) on
any error.

### 5.3 Memory and retrieval

`EpisodicMemoryStore` writes one `AgentEpisode` per run with the task text, a summary, and tool usage
counts. Recall loads the most recent episodes with the same task type and course, scores them in memory
with character-trigram Jaccard similarity against the task text, and orders by same learner first, then
similarity. The embedding column exists but is never filled.

`KnowledgeRetriever` extracts up to twelve search terms, scopes by course and module, and fetches up to
200 candidates with `FREETEXT` on SQL Server when the table has an active full-text index, or with an
escaped `LIKE` filter otherwise. Candidates are scored in memory with weights of 3 for the question, 2
for tags, and 1 for the answer.

`Compactor` supplies the server-side compaction configuration (beta `compact-2026-01-12` with an
input-token trigger) and doubles as the episode summarizer: a low-effort call on the cheap model with a
512 token cap, falling back to an extractive summary on refusal, empty output, or error. The summary
call is neither charged to the run nor recorded as evidence.

`CacheHealthMonitor` records cache read and creation tokens per call and, when the same prefix hash
repeats within a run but reads zero cache tokens, increments a miss counter and logs a warning.

---

## 6. Execution Plane

### 6.1 Registry and native tools

`ToolRegistry` is built per scope from every registered `IToolHandler` plus the handlers of every
`IToolSource`, stored in a name-sorted dictionary; duplicate names throw.

`NativeTool<TInput, TOutput>` is the base for typed tools. It exports input and output schemas from the
records, builds the descriptor with `Strict = true`, deserializes the input, calls the typed
`ExecuteAsync`, and returns the output as a JSON payload.

| Tool | Risk | Course-scope argument | Exclusive | Behaviour |
|---|---|---|---|---|
| `run_lab_code` | ReadOnly | none | no | Resolves the language's argv template, runs the files through `ISandbox`, and compares trimmed stdout with the expected output |
| `lookup_knowledge` | ReadOnly | `course_id` | yes | Searches the knowledge base, up to five entries |
| `create_escalation` | Mutating | `course_id` | yes | Inserts an `AIEscalation` and saves inside the stage transaction |
| `notify_learner` | External | `course_id` | yes | Records a `LearnerNotified` analytics event; no delivery channel exists |

### 6.2 MCP tools

`McpToolSource` connects once at host start, under a semaphore, to every server in `Agents:Mcp:Servers`
through `ConfiguredMcpTransportFactory` (stdio or HTTP transports from the official MCP SDK). It lists
each server's tools and registers an `McpToolHandler` per tool under `{server}{separator}{tool}`, with
the risk level from `RiskOverrides` or `External` by default, and `Strict = false`. A handler forwards
the input properties as arguments with a call timeout, joins text blocks into the content, and exposes
structured content as the payload. Connection or listing failures log a warning and skip the server;
the host always starts. `McpToolSourceHostedService` drives the connection and disposal.

### 6.3 Sandboxes

`DockerSandbox` is the registered `ISandbox`. Per request it writes the files to a temporary folder,
ensures the image (pulling when allowed), creates a container with networking disabled, memory and
swap set to the same limit, a CPU quota, a PID limit, all capabilities dropped, and no new privileges,
bind-mounts the folder at the working directory, starts the container, waits up to the timeout and
kills it on expiry, reads the demultiplexed logs into capped buffers, inspects for an OOM kill, and
always removes the container and the folder. Any exception becomes a failed result rather than a thrown
error.

`WasmSandbox` runs a WASI command module with Wasmtime under fuel, an epoch-based wall-clock deadline,
and a memory cap, with the other files pre-opened read-only. It is registered and tested but nothing
routes runtime calls to it.

### 6.4 ToolDispatcher pipeline

`DispatchAsync(run, calls, stepId)` runs in three phases:

1. **Decide, sequentially.** Every call goes through the policy decision point before anything
   executes. An unknown tool is denied. The first `RequireApproval` verdict returns immediately with the
   pending call and nothing executes.
2. **Execute.** Allowed, non-replayed calls run concurrently, except exclusive handlers, which run one
   at a time after the concurrent batch. A denied or rate-limited verdict becomes an error result
   without execution. A replayed result from the idempotency barrier is returned as-is. Identical calls
   in the same batch coalesce on their idempotency key so the handler runs once. Handler exceptions
   become `ToolFailure` results. Successful outputs are validated against the tool's output schema by
   `ToolOutputValidator`, and an invalid output becomes `OutputInvalid`.
3. **Record, in call order.** Non-error, non-replayed calls with an idempotency key are recorded in the
   barrier. Content is truncated with a marker. A tool-call span is started with the policy decision,
   rule, replay flag, and error flag, backdated by the measured latency. A `ToolCall` trace is appended,
   and a `SandboxLog` trace when the payload has the sandbox result shape.

All results come back in call order in one `DispatchResult`, so the next model turn sees every result
in a single user message.

---

## 7. Control Plane

### 7.1 PolicyDecisionPoint

Rules come from `Agents:Policy:Rules`, or the built-in defaults when none are configured. A rule has a
kind, an enabled flag, a tool pattern (exact, `prefix*`, or `*`), a risk-level filter, and
kind-specific settings. Evaluation order is fixed by kind:

1. **Identity.** No principal or an unauthenticated one is denied. Each matching rule can require roles
   and, by default, require the call's course argument to be inside the principal's course scope.
2. **CourseMaterial.** If the task contract reports that the call changes protected material, the first
   matching rule's effect applies; `RequireApproval` is satisfied by a recorded approval for the exact
   call.
3. **Risk.** The first matching rule decides: `Allow`, `Deny`, `AllowInScope` (allowed when the call's
   course equals the run's course, approval otherwise), or `RequireApproval`. A recorded approval for
   the exact run, tool, and argument hash turns a required approval into Allow. No matching rule fails
   closed to `RequireApproval`.
4. **RateLimit.** `ToolRateLimiter` takes a permit from every matching rule's sliding window,
   partitioned per run or per user. A rejected permit is a denial with a retry-after hint.
5. **Idempotency.** For calls a matching rule covers, the barrier computes the key and looks it up. A hit
   returns Allow with the stored result for replay; a miss returns Allow with the key so the dispatcher
   records the result afterwards.

Default rules, in evaluation order: `enrolment`, `course-material-approval`, `read-only` (Allow),
`mutating-in-scope` (AllowInScope), `destructive-approval`, `external-approval`, `per-run` (20 per
minute), `per-user` (60 per minute), `idempotent-mutations` (Mutating, Destructive, External).

### 7.2 IdempotencyBarrier, ToolRateLimiter, ApprovalGate

`IdempotencyBarrier` keys on the SHA-256 of the canonical arguments; the full key is run, tool name,
and hash, enforced by a unique index. Lookups check the change tracker first so a call repeated within
the same stage is caught before commit. Only successful results are recorded, so a failed mutation may
run again on retry.

`ToolRateLimiter` is an in-process singleton with one partitioned sliding-window limiter per rate-limit
rule. Permits are not refunded when a later rule rejects, and state is not shared between host
instances.

`ApprovalGate.RequestAsync` adds a pending `AgentApproval` with the call, its argument hash, the reason,
and the rule that fired, plus a medium-priority `AIEscalation` and an `Approval` trace; the runner
commits them with the stage. `DecideAsync` sets the decision, returns a parked run to `Running` with a
new concurrency stamp, resolves the linked escalation, records a trace, saves immediately, and enqueues
the run. On the Admin host the queue is a no-op and the Web worker's sweep picks the run up.

---

## 8. Evidence Plane

`OtelEvidenceSink` owns the span model and the trace rows. `StartRun` creates the `agent.run` root span,
stores its trace and span ids on the run, and stops it at once; every later stage span on any worker
parents to those ids. `StartStage`, `StartModelCall`, and `StartToolCall` create child spans with the
run, task, course, user, stage, model, effort, tool, and risk tags. `RecordAsync` sets the run id and
timestamp on an `AgentTrace`, adds it to the context, records token and cost metrics for model calls,
annotates the current span with the `gen_ai.*` and `agent.*` attributes, and adds an `agent.{Kind}`
event. `FinishRunAsync` writes the `AgentRunFinished` analytics event with the run's totals.

`TraceWriter` is a static builder for the three heavyweight row kinds: `ModelCall` (the envelope as
sent, the raw response, content blocks, reasoning summary, stop reason, effort, latency, cost),
`ToolCall` (the call, decision, rule, risk level, replay and coalesce flags, latency, result), and
`SandboxLog` (recognized from any result payload with `sandbox`, `exit_code`, `stdout`, and `stderr`).

`CostAccountant` prices each token class from `Agents:Pricing:Models` (exact match, then longest
prefix; unpriced models cost zero with a one-time warning), adds the tokens to the run's totals and the
budget, and adds the cost to the run.

Append-only enforcement lives in `IntelligentlyDbContext`: both `SaveChanges` overrides throw
`AppendOnlyViolationException` if any tracked `AgentTrace` is modified or deleted.

---

## 9. Learn and task contracts

`EpisodeWriter` is the single writer of episodes. It returns an existing episode for the run if one
exists, otherwise builds a text transcript (task, plan, each step's status, verifier message, assistant
text, and capped tool results), summarizes it through `IEpisodeSummarizer`, counts tool usage, and
writes through the memory store without saving; the caller commits.

`EscalationResolver` marks an escalation resolved with the instructor's response and, when asked,
creates or reuses one `AIKnowledgeEntry` per escalation (entry type derived from the task type name),
ensures the run's episode exists, links the episode to the entry, and records a Learn-stage note.

`TaskTypeRegistry` maps task type names to `ITaskContract` implementations, case-insensitively. A task
with no contract still runs: Interpret only requires a non-empty JSON object, Verify applies only the
generic checks, and Learn writes no learner output.

`LabErrorDiagnosisContract` is the one contract shipped:

- **Spec validation.** JSON Schema from the spec record, then rules: the course id matches the run, the
  language and error text are non-empty, at least one learner file, canonical unique relative paths,
  and an entrypoint among the files.
- **Plan template.** Five phases: `reproduce` and `rerun` with `run_lab_code`, `diagnose`,
  `propose_fix`, and `explain` without a tool, with constraints on ordering and a cap of ten steps.
- **Plan validation.** Phase names known, `run_lab_code` only in reproduce or rerun, reproduce before
  rerun, explain last, at most ten steps.
- **Step verification.** Every successful sandbox result must match its call and the spec's language,
  entrypoint, and paths. Reproduce must run the original submission and must reproduce the error.
  Rerun must exit zero with no compiler-error pattern in its output and, for graded labs, stdout equal
  to the expected output. Explain must have text; on a graded lab it passes only after a passed rerun,
  on an ungraded lab it defers to the judge with a minimum score of 0.7.
- **Protected changes.** A `run_lab_code` submission whose content differs from any course file is
  reported to the policy engine as a protected change.
- **Learner output.** The explanation is the text of the last passed explain step; the fix diff is a
  unified diff of the learner's files against the last successful non-reproduce submission, with
  removed files shown as deletions and new files as additions.

---

## 10. Hosting and dependency injection

`AddAgentRuntime(services, configuration)` in `AgentServiceCollectionExtensions` registers every plane.
Lifetimes follow one rule: anything that touches the scoped DbContext is scoped; anything stateless or
holding process-wide state is a singleton.

| Lifetime | Registrations |
|---|---|
| Singleton | `RunQueue`, `ProcessWorkerIdentity`, `TaskTypeRegistry` and `LabErrorDiagnosisContract`, `AnthropicReasoningEngine`, `CacheHealthMonitor`, `AnthropicTokenCounter`, `TokenBudgeter`, `Compactor` (also as `IEpisodeSummarizer`), `DockerSandbox` (also as `ISandbox`), `WasmSandbox`, `ConfiguredMcpTransportFactory`, `McpToolSource` (also as `IToolSource`), `ToolOutputValidator`, `ToolRateLimiter`, `CostAccountant` |
| Scoped | `StateGraphRunner`, `AgentRunService`, the eight stage handlers, `PlanValidator`, `CandidateSearch`, `KnowledgeRetriever`, `EpisodicMemoryStore`, `ContextAssembler`, the four native tools, `ToolRegistry`, `ToolDispatcher`, `IdempotencyBarrier`, `ApprovalGate`, `PolicyDecisionPoint`, `OtelEvidenceSink`, `EpisodeWriter`, `EscalationResolver` |
| Hosted | `McpToolSourceHostedService` |

The **Web** host adds the DbContext, Identity, the HTTP rate limiter, the Anthropic client, the agent
runtime, `RunWorker` (after the MCP hosted service, so servers connect first), OpenTelemetry tracing and
metrics, and the middleware order routing, authentication, rate limiting, authorization, Razor Pages,
and the minimal API group.

The **Admin** host adds the same DbContext, Anthropic client, agent runtime, and Identity, then replaces
`IRunQueue` with `NoWorkerRunQueue`, a no-op. It has no worker, no OpenTelemetry, and no rate limiter.

`RunWorker` is a `BackgroundService` that drains the queue one run at a time. For each run it opens a
new scope per stage, calls `TryStepAsync`, and stops when the run is no longer `Running`, when the claim
fails, after an error, or after 500 stages. A concurrent sweep re-enqueues every `Running` run at
startup and, on a timer, every `Running` run that is parked at `AwaitingApproval` or whose lease has
expired.

`AgentRunService.CreateAsync` builds the run with the configured budget and the caller's principal,
starts the root span, saves, and enqueues.

---

## 11. Extension points

**A new native tool.** Define input and output records with `[JsonPropertyName]` and `[Description]`,
subclass `NativeTool<TIn, TOut>`, pass the name, description, risk level, course-scope argument, and
exclusive flag, implement the typed `ExecuteAsync`, and register it as a scoped `IToolHandler`. It then
appears in every envelope and the plan instruction, passes plan validation, is governed by the policy
rules for its risk level, is covered by idempotency when a rule matches, and produces evidence rows
without further code. A hand-written `IToolHandler` works the same way for tools that need a custom
schema. Dynamic tool sets implement `IToolSource`.

**A new task type.** Add a spec record, implement `ITaskContract` following the lab contract, and
register it as a singleton `ITaskContract`. Optionally list the type under
`Agents:CandidateSearch:TaskTypes`.

**A new policy rule.** Rules are configuration. A configured list replaces the defaults entirely, so
restate the defaults you keep. A new rule kind needs code: a `PolicyRuleKind` value, a step in
`PolicyDecisionPoint`, and limiter support if it limits.

**A new stage.** Add the `AgentStage` value, the edges in `StageTransitions`, a status mapping in the
runner if it is not `Running`, an instruction branch in `ContextAssembler` if it calls the model, a
handler registered as scoped `IStageHandler`, and a review of the worker's sweep conditions if the
stage parks the run. Two handlers for one stage throw at runner construction.

**Another sandbox.** Register a different `ISandbox`. There is no per-request sandbox selection.

---

## 12. Where the code departs from the plan

| Plan item | Status in code |
|---|---|
| Cognitive router choosing model and effort per task class | Not implemented; model and effort are fixed per operation |
| Server-side fallbacks on refusal | Not implemented; a refusal throws and fails the run |
| Streaming for large reasoning calls | Not implemented in the reasoning engine |
| WebAssembly sandbox in the tool path | Implemented and tested, not wired to any tool |
| Embeddings and vector search | Not implemented; the embedding column is never filled |
| Approval timeout | Not implemented; a pending approval waits indefinitely |
| Admin policy editor | Not implemented; rules are configuration only |
| Per-step retry budget | The retry counter is run-wide |
| Learner notification delivery | Records an event only |
| Episode summary and token-count calls | Not charged to the run and not recorded as evidence |
| Tool-call spans wrapping execution | Created after execution with a backdated start |
| Cross-instance queue and rate limits | Both are per process; hand-off between instances relies on the database sweep |
