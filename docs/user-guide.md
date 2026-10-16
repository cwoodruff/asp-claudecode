# User and Operator Guide

This guide explains how to set up, configure, run, and operate Intelligently and its autonomous
tutoring agent. It is written for three readers: a developer setting the system up, a learner or
instructor using it, and an operator keeping it running.

Related documents: [Architecture](architecture.md), [Software Design](software-design.md),
[Database Design](database-design.md).

---

## 1. Setup

### 1.1 Prerequisites

| Requirement | Needed for | Notes |
|---|---|---|
| .NET SDK able to build `net10.0` | Everything | No `global.json` pins a version. The last verified build used SDK 11.0.100-rc.1. |
| EF Core CLI (`dotnet ef`) | Applying migrations | Install globally: `dotnet tool install --global dotnet-ef`. The repo has no local tool manifest. |
| SQL Server or LocalDB | Everything | Development settings point at `(localdb)\MSSQLLocalDB`, database `Intelligently`. Full-text search is optional; without it, knowledge lookup falls back to `LIKE` matching. |
| Anthropic API key | Any agent run | Read from `Anthropic:ApiKey`. Checked the first time the client is used, not at startup. |
| Docker Desktop | The `run_lab_code` tool and the lab error diagnosis task | The hosts start without Docker; lab runs then fail and escalate. The sandbox image is `mcr.microsoft.com/dotnet/sdk:10.0`, about 1.3 GB, pulled on first use. |
| Browser internet access | The web UI | htmx is loaded from unpkg.com. |

Wasmtime ships as a NuGet package and needs no separate install.

### 1.2 Secrets

Both hosts share one user-secrets store with the id `intelligently-local`, so a secret set through the
Web project also serves Admin. User secrets are loaded only in the Development environment.

```bash
dotnet user-secrets set "Anthropic:ApiKey" "<your key>" --project src/Intelligently.Web
```

For other environments, use environment variables in the double-underscore form:
`Anthropic__ApiKey`, `ConnectionStrings__DefaultConnection`.

The production `appsettings.json` files contain neither a connection string nor an API key. Both must
be supplied by the environment.

### 1.3 Build, migrate, run

```bash
dotnet build
```

Apply the migrations. The design-time factory reads the connection string only from the
`ConnectionStrings__DefaultConnection` environment variable and falls back to LocalDB. It does not read
appsettings or user secrets.

```bash
dotnet ef database update --project src/Intelligently.Infrastructure --startup-project src/Intelligently.Infrastructure
```

Neither host applies migrations at startup. Run the command above after every pull that adds a
migration.

Start the learner app:

```bash
dotnet run --project src/Intelligently.Web
```

Start the instructor portal:

```bash
dotnet run --project src/Intelligently.Admin
```

| Host | HTTP | HTTPS |
|---|---|---|
| Web (learners, run worker) | http://localhost:5155 | https://localhost:7038 |
| Admin (instructors) | http://localhost:5001 | https://localhost:7263 |

Both hosts redirect HTTP to HTTPS. Only Web runs the background worker that executes runs; Admin reads
and decides but never steps a run, so a Web instance must be running for anything to progress.

### 1.4 Accounts

There is no registration page and no default account: a fresh database has no users at all. Accounts,
the `Instructor` role, and course enrolments are created with the account tool in `src/Intelligently.Tools`,
a console app that goes through the same ASP.NET Core Identity registration as the hosts
(`AddIntelligentlyIdentity`), so password rules, hashing, and user-name normalisation are identical.

What each kind of user needs:

| User | Identity role | Claims |
|---|---|---|
| Learner | none required | One `intelligently:course` claim per enrolled course, value = course id |
| Instructor | `Instructor` | `intelligently:course` claims as well, if they will create runs |

**Create the first instructor** after applying the migrations (section 1.3):

```bash
dotnet run --project src/Intelligently.Tools -- create-user --username instructor1 --email instructor1@example.edu --instructor --course 1
```

The tool asks for the password twice with the input masked. It never accepts a password as a
command-line argument (`--password` and similar options are rejected) and never prints one. For scripted
use, put the password in the `INTELLIGENTLY_NEW_PASSWORD` environment variable instead; the tool reads it
when set and does not prompt. With stdin redirected and the variable unset, it fails rather than reading
a password in plain text.

Commands (each takes `--username`; `--course` may be repeated):

| Command | Effect |
|---|---|
| `create-user --username X --email Y [--instructor] [--course N]...` | Creates the user with a password, adds the `Instructor` role (creating the role if it does not exist yet) and one `intelligently:course` claim per course. All of it is one transaction: a rejected password or any other error leaves nothing behind. |
| `grant-course --username X --course N...` | Adds the enrolment claims the user does not already have. |
| `revoke-course --username X --course N...` | Removes every enrolment claim for those courses. |
| `add-instructor --username X` | Adds the `Instructor` role, creating the role if needed. |
| `remove-instructor --username X` | Removes the user from the `Instructor` role. The role itself stays. |
| `reset-password --username X` | Sets a new password (prompted or from `INTELLIGENTLY_NEW_PASSWORD`), clears any lockout, and changes the security stamp. |
| `list-users` | Prints each user's name, email, roles, enrolled courses, and lockout end. No password hashes or stamps. |
| `help` | Prints the usage. |

Exit codes: 0 on success (including "already enrolled" and similar no-ops), 1 when a command fails, 2 for
an invalid command line.

The tool reads `ConnectionStrings:DefaultConnection` the way the hosts do: `appsettings.json`,
`appsettings.{Environment}.json`, user secrets (id `intelligently-local`, Development only), then
environment variables (`ConnectionStrings__DefaultConnection`). The environment comes from
`DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT`. `dotnet run` uses the tool's launch profile, which sets
`Development`, so by default it targets the same LocalDB database as the hosts. For another server,
set the environment variable:

```bash
ConnectionStrings__DefaultConnection="Server=...;Database=Intelligently;..." dotnet run --project src/Intelligently.Tools -- list-users
```

Course ids are positive whole numbers. Nothing checks that a course exists; there is no course entity yet.

Claims and roles are copied into the cookie at sign-in. A user who is already signed in picks up a role or
enrolment change when they next sign in, or when the security stamp validator next refreshes the cookie
(every 30 minutes, the Identity default). A password reset changes the security stamp, so existing
sign-ins end at that refresh.

Password rules: at least 8 characters, with at least one uppercase letter, one lowercase letter, one digit,
and one non-alphanumeric character. The hosts set only the length and keep ASP.NET Core Identity's other
defaults, and the tool uses the same options. Lockout after 5 failed attempts for 15 minutes;
`reset-password` clears it.

Sign-in routes are the same in both hosts: `/Account/Login`, `/Account/Logout` (POST), and
`/Account/AccessDenied`. Browser requests that fail authorization are redirected; JSON and htmx requests
receive a bare 401 or 403.

---

## 2. Using the system as a learner

### 2.1 The demo form

Sign in to Web and open `/agent`. The page shows the signed-in user and their enrolled courses, and a
form with three fields:

- **Course id.** Defaults to 1, or to the first enrolled course.
- **Task type.** Defaults to `LabErrorDiagnosis`.
- **Task spec.** A JSON textarea pre-filled with a sample lab: a `Program.cs` that uses Newtonsoft.Json
  and the compile error that produces.

Submitting creates a run and swaps a live status panel into the page. The panel polls every two seconds
while the run is Running or Waiting. The form is rejected inline when the spec is not valid JSON or the
course is not in the user's enrolment.

### 2.2 The JSON API

`POST /agent/runs` creates a run from a JSON body. It requires the Identity cookie and is rate limited
to 10 requests per minute per user.

```json
{
  "courseId": 1,
  "taskType": "LabErrorDiagnosis",
  "taskSpec": {
    "course_id": 1,
    "module_id": 3,
    "language": "csharp",
    "learner_files": [
      { "path": "Program.cs", "content": "Console.WriteLine(\"Hello, lab!\")\n" }
    ],
    "course_files": [],
    "entrypoint": "Program.cs",
    "error_text": "Program.cs(1,35): error CS1002: ; expected",
    "expected_output": "Hello, lab!\n",
    "learner_level": "Beginner"
  }
}
```

A `userId` in the body is ignored. The run's principal comes from the caller's claims only.

The response is `201 Created` with a `Location` header pointing at `/agent/runs/{id}` and a body with
`id`, `stage`, `status`, `statusReason`, `planRevision`, `currentStepIndex`, `createdAt`, and
`completedAt`. A new run starts at stage Interpret with status Running.

| Status | When |
|---|---|
| 400 | Empty task type, or a task spec that is not a JSON object |
| 401 | Not signed in |
| 403 | The course is not in the caller's enrolment |
| 429 | More than 10 runs created in a minute; a `Retry-After` header is included when known |

A spec that is a JSON object but fails the task's validation rules is accepted with 201. The run then
fails at Interpret with a status reason that names the problem.

There is no JSON endpoint for reading a run. `GET /agent/runs/{id}` returns HTML: the full page, or
only the status panel when the request carries the `HX-Request` header. Only the run's owner or an
Instructor may view it.

### 2.3 The lab error diagnosis task

| Field | Type | Rules |
|---|---|---|
| `course_id` | int | Must equal the run's course |
| `module_id` | int or null | |
| `language` | string | One of `csharp`, `csproj`, `fsharp`, `shell`, `python` |
| `learner_files` | array of `{path, content}` | At least one. Relative, unique paths. The agent may change these |
| `course_files` | array of `{path, content}` or null | Read-only course material. A fix that changes one needs instructor approval |
| `entrypoint` | string | Must be one of the file paths |
| `error_text` | string | Non-empty |
| `expected_output` | string or null | When present, the fix is graded by comparing sandbox output. When null, the explanation is judged by the model against a rubric with a minimum score of 0.7 |
| `learner_level` | string | `Beginner`, `Intermediate`, or `Advanced` |

The agent follows a five-phase plan: reproduce the error in a sandbox, diagnose it, propose a fix, run
the fixed code, and explain the problem at the learner's level.

### 2.4 What the learner sees

While the run is active the status panel shows the stage, status, plan revision and step, the remaining
budget, and the last transition. When the run is waiting for an instructor it says so.

When the run completes, the panel shows a section headed "What went wrong, and the fix" with the
explanation, followed by a unified diff of the learner's files under "Proposed fix to your files", or a
note that no file changes were needed.

When the run fails or is escalated, the panel shows the status and the reason, for example
"Escalated to instructor: ...". No explanation is shown in that case.

---

## 3. Using the system as an instructor

Sign in to Admin with an account in the `Instructor` role. The navigation offers Approvals, Runs, and
Escalations. All three require the Instructor role.

### 3.1 Approvals

`/approvals` lists pending tool calls that the Control Plane would not execute on its own, oldest first.
Each row shows when it was requested, a link to the run, the tool name, the arguments as JSON, and the
reason approval was required.

Approve or deny each row, with an optional note. What happens next:

- **Approve.** The run resumes at the same step and executes the exact call that was approved. No new
  model turn is needed.
- **Deny.** The step is marked failed with the note. If the rule that fired was the course-material
  rule, the run escalates and fails. Otherwise the run replans without that tool, or escalates if no
  replans remain.

Decisions take effect through the Web host's sweep, which runs every 15 seconds by default
(`Agents:ApprovalSweepSeconds`). Admin never executes a run itself. A second decision on the same row
returns 409.

There is no approval timeout. A pending approval waits until someone decides it.

Which calls need approval under the default policy:

| Tool risk level | Default outcome |
|---|---|
| ReadOnly | Allowed |
| Mutating | Allowed inside the run's course scope, approval otherwise |
| Destructive | Approval |
| External (the default for every MCP tool) | Approval |
| Any `run_lab_code` call that changes a course file | Approval |

Requesting an approval also raises a medium-priority escalation, so it appears on the Escalations page
and is resolved automatically when the approval is decided.

### 3.2 Runs and the trace viewer

`/runs` lists the 50 most recent runs with their user, task, stage, status, and plan revision.

`/runs/{id}` is the read-only trace viewer. It shows:

- **Run.** Task, user with roles and courses, stage, status, remaining budget, token totals, cost (the
  run's total and the sum of the evidence rows), trace id, the episode written at the end, the task
  spec, and the learner-facing output.
- **Plans.** Every plan revision with its goal and ordered steps, and every planner, candidate, and
  judge model call.
- **Approvals.** Each request and its decision.
- **Steps.** For each attempt: status, verifier message, tokens, and cost. Under it, the model call
  with its reasoning summary, the full prompt envelope as sent, the raw response and content blocks,
  then each tool call with the policy decision, risk level, input, result, and payload, and each
  sandbox log with stdout, stderr, and exit code.
- **Traces.** Every evidence row in order with a button that loads the raw JSON payload on demand.

Evidence rows are append-only and cannot be edited.

### 3.3 Escalations and the knowledge base

`/escalations` lists escalations raised by agent runs, open ones first. Three things raise them: the
Recover stage when a run cannot recover (high priority), an approval request (medium priority, resolved
with the approval), and the `create_escalation` tool when the agent decides to ask for help.

Resolve an escalation by writing a response and optionally ticking **Add to knowledge base**. With the
box ticked, the system creates one knowledge entry for the escalation, with the learner's question, the
instructor's answer, tags, and an entry type derived from the task type, and links it to the run's
episode. Later runs in the same course retrieve the entry through the `lookup_knowledge` tool and the
context assembler.

This is the only way knowledge entries are created through the UI.

---

## 4. Configuration reference

All agent settings live under the `Agents` section and are bound to `AgentsOptions`. Both hosts carry
the same `Agents` block in `appsettings.json`.

Two rules about overriding values:

- Arrays merge by index across configuration sources. To turn off candidate search, set
  `Agents:CandidateSearch:Candidates` to 1 rather than emptying `TaskTypes`.
- Any custom `csharp` entry under `Agents:Sandbox:Languages` must keep `-p:PublishAot=false`, or
  `dotnet run` in the offline sandbox tries to restore the AOT compiler and fails.

### 4.1 Top level

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | LocalDB in Development, none otherwise | SQL Server connection |
| `Anthropic:ApiKey` | none | Required for any model call |
| `Otel:Endpoint` | `http://localhost:4317` (Web only) | OTLP endpoint, used outside Development |
| `Agents:DefaultModel` | `claude-opus-5-5` | Model for planning, acting, verification, judging, and token counting |
| `Agents:CheapModel` | `claude-haiku-5-5` | Model for episode summaries |
| `Agents:QueueCapacity` | 100 | Bounded in-process run queue |
| `Agents:MaxToolResultChars` | 16000 | Tool result content truncation |
| `Agents:ApprovalSweepSeconds` | 15 | Worker sweep interval for decided approvals and expired leases; 0 disables the periodic sweep |
| `Agents:StageLeaseSeconds` | 600 | How long a worker holds a run per stage. Must exceed the longest stage |

### 4.2 Budgets

Seeded onto each run when it is created. The counters only decrease.

| Key | Default | Meaning |
|---|---|---|
| `Agents:Budgets:ReplansLeft` | 3 | Replans allowed |
| `Agents:Budgets:RetriesLeft` | 2 | Retries of the same step |
| `Agents:Budgets:ToolCallsLeft` | 40 | Tool calls per run |
| `Agents:Budgets:TokensLeft` | 300000 | Hard token budget, all token classes |
| `Agents:Budgets:WallClockMinutes` | 30 | Wall-clock budget |
| `Agents:Budgets:PromptTokens` | 150000 | Input budget for one prompt envelope |
| `Agents:Budgets:StagePromptTokens` | `{}` | Per-stage override keyed by stage name |

### 4.3 Context

| Key | Default | Meaning |
|---|---|---|
| `Agents:Context:KnowledgeEntries` | 8 | Knowledge entries per envelope |
| `Agents:Context:Episodes` | 5 | Episodes recalled per envelope |
| `Agents:Context:EpisodeCandidates` | 200 | Recent episodes scored for similarity |
| `Agents:Context:CompactAfterTokens` | 100000 | Prompt size that triggers server-side compaction |
| `Agents:Context:SummaryMaxChars` | 2000 | Episode summary length |
| `Agents:Context:SummaryInputMaxChars` | 24000 | Transcript length given to the summarizer |

### 4.4 Policy rules

`Agents:Policy:Rules` is a list. When it is empty, the built-in defaults apply. Rules run in kind order
(Identity, CourseMaterial, Risk, RateLimit, Idempotency), then in configuration order within a kind. A
tool with no matching risk rule fails closed to RequireApproval.

| Field | Default | Used by |
|---|---|---|
| `Name` | "" | all |
| `Kind` | `Identity` | `Identity`, `CourseMaterial`, `Risk`, `RateLimit`, `Idempotency` |
| `Enabled` | true | all |
| `Tool` | `*` | Exact name, `prefix*`, or `*` |
| `RiskLevels` | all | `ReadOnly`, `Mutating`, `Destructive`, `External` |
| `RequiredRoles` | none | Identity |
| `RequireCourseScope` | true | Identity |
| `Effect` | `RequireApproval` | Risk and CourseMaterial: `Allow`, `AllowInScope`, `Deny`, `RequireApproval` |
| `Scope` | `Run` | RateLimit: `Run` or `User` |
| `PermitLimit` | 20 | RateLimit |
| `WindowSeconds` | 60 | RateLimit |

The default rules, in order: `enrolment` (Identity, course scope required), `course-material-approval`,
`read-only` (Allow), `mutating-in-scope` (AllowInScope), `destructive-approval`, `external-approval`,
`per-run` (20 calls per minute), `per-user` (60 calls per minute), and `idempotent-mutations` (Mutating,
Destructive, and External calls are keyed and replayed).

### 4.5 MCP servers

Servers connect once at host start. Add an entry and restart.

```json
"Mcp": {
  "Servers": [
    { "Name": "docs", "Transport": "stdio", "Command": "npx", "Args": ["-y", "some-mcp-server"] },
    { "Name": "remote", "Transport": "http", "Url": "https://host/mcp", "Headers": { "Authorization": "Bearer ..." } }
  ],
  "RiskOverrides": { "docs__search": "ReadOnly" },
  "NameSeparator": "__",
  "ConnectTimeoutSeconds": 30,
  "CallTimeoutSeconds": 120
}
```

Each tool is registered as `{server}{separator}{tool}`. Its risk level comes from `RiskOverrides`,
matched on the registered name first and the bare tool name second, and defaults to External, which
means every call needs approval until an override says otherwise. A server that fails to connect or
list tools is logged and skipped; the host still starts.

### 4.6 Sandbox

| Key | Default | Meaning |
|---|---|---|
| `Agents:Sandbox:Image` | `mcr.microsoft.com/dotnet/sdk:10.0` | Container image |
| `Agents:Sandbox:DockerEndpoint` | unset | Overrides `DOCKER_HOST` and the platform default |
| `Agents:Sandbox:PullImage` | true | Pull the image when missing |
| `Agents:Sandbox:TimeoutSeconds` | 60 | Wall-clock limit, then the container is killed |
| `Agents:Sandbox:MemoryBytes` | 536870912 | Memory limit; swap is set to the same value |
| `Agents:Sandbox:Cpus` | 1.0 | CPU limit |
| `Agents:Sandbox:PidsLimit` | 256 | Process limit |
| `Agents:Sandbox:MaxOutputBytes` | 65536 | Cap on stdout and on stderr |
| `Agents:Sandbox:WorkingDirectory` | `/work` | Mount point for the submitted files |
| `Agents:Sandbox:Languages` | see below | Map of language to argv template with `{entrypoint}` |
| `Agents:Sandbox:Wasm:*` | fuel 2e9, 64 MB, 10 s, 64 KB output | WebAssembly sandbox limits |

Default languages: `csharp` runs `dotnet run {entrypoint} -p:PublishAot=false`, `csproj` runs
`dotnet run --project {entrypoint}`, `fsharp` runs `dotnet fsi {entrypoint}`, `shell` runs
`sh {entrypoint}`, `python` runs `python3 {entrypoint}`.

Containers run with networking disabled, all capabilities dropped, and no new privileges.

### 4.7 Pricing and candidate search

`Agents:Pricing:Models` maps a model id to `InputPerMillion`, `OutputPerMillion`, `CacheReadPerMillion`,
and `CacheCreationPerMillion` in USD. Defaults cover `claude-opus-5-5` and `claude-haiku-5-5`. Lookup is
exact, then longest prefix. An unpriced model is charged at zero and logged once.

`Agents:CandidateSearch:Candidates` (default 3) and `Agents:CandidateSearch:TaskTypes` (empty by default,
`["LabErrorDiagnosis"]` in Web's Development settings) control test-time plan search. The search is on
when `Candidates` is greater than 1 and the task type is listed. Every candidate plan and judge call is
recorded as evidence and charged to the run.

---

## 5. Operating the system

### 5.1 The run worker

The Web host runs one background worker. Runs enter a bounded in-process queue when they are created,
when an approval is decided on the same host, and from two sweeps:

- **At startup**, every run with status Running is re-enqueued.
- **Every `ApprovalSweepSeconds`**, every Running run that is either waiting at AwaitingApproval or
  whose lease has expired is re-enqueued.

For each run the worker claims it with a conditional update, executes one stage inside a transaction,
commits, and repeats up to 500 stages before parking it. A run whose claim fails is held by another
worker and is dropped from this worker's queue. An unhandled error in a stage fails the run with the
error in its status reason.

### 5.2 Running more than one Web instance

Point every instance at the same database. Leases guarantee that only one instance executes a given
stage, and a worker whose lease was taken over cannot commit a stale stage. A run created on one
instance is picked up by another only through the sweeps, so set `StageLeaseSeconds` longer than the
slowest stage and keep `ApprovalSweepSeconds` enabled. The tool rate limiter and the HTTP rate limiters
are per process.

### 5.3 Telemetry

OpenTelemetry is configured in Web only. In Development, traces and metrics go to the console. In other
environments they go to the OTLP endpoint in `Otel:Endpoint`.

Spans: one `agent.run` root span per run, whose ids are stored on the run so stage spans from any
worker attach to it; `agent.stage.{Stage}` per stage; `agent.model_call` per model call with the
`gen_ai.*` token attributes; `agent.tool_call.{name}` per tool call with the policy decision and risk
level.

Metrics on the `Intelligently.Agents` meter:

| Instrument | Meaning |
|---|---|
| `agent.tokens` | Tokens by model, stage, and class (input, output, cache read, cache creation) |
| `agent.cost_usd` | Cost by model and stage |
| `agent.stage.duration` | Stage latency histogram |
| `agent.cache.read_input_tokens`, `agent.cache.creation_input_tokens` | Cache activity |
| `agent.cache.repeated_prefix_misses` | Calls whose repeated prefix read zero cache tokens |

Cost totals appear on the run in the Admin trace viewer, on each model call evidence row, in the
`agent.cost_usd` metric, and in one `AgentRunFinished` analytics event per finished run.

Logs use standard ASP.NET Core logging. Serilog is referenced but not configured, and logs carry no trace
id, so correlate through the trace id shown in the trace viewer.

### 5.4 Tools

| Tool | Risk | What it does |
|---|---|---|
| `run_lab_code` | ReadOnly | Runs submitted files in the Docker sandbox and compares output to an expectation |
| `lookup_knowledge` | ReadOnly | Searches the knowledge base for the run's course |
| `create_escalation` | Mutating | Raises an instructor escalation |
| `notify_learner` | External | Records a notification event; no real delivery channel exists yet |

The WebAssembly sandbox is implemented and tested but not attached to any tool; `run_lab_code` always
uses Docker.

---

## 6. Testing and evaluation

```bash
dotnet test
```

```bash
dotnet test tests/Intelligently.IntegrationTests/ --filter Category=Eval
```

Unit tests run the real runtime on SQLite in-memory with a mocked reasoning engine. Integration tests
host Web and Admin with `WebApplicationFactory`, a header-based test authentication handler
(`X-Test-User`, `X-Test-Roles`, `X-Test-Courses`), and a fake sandbox that prints "hi". Docker sandbox
tests skip with a reason when the engine is not reachable.

The eval harness replays stored run fixtures in `tests/Intelligently.IntegrationTests/Evals/Fixtures`
through a deterministic rubric and asserts a pass rate of at least 0.95 per fixture and overall
(`Evals:PassRateThreshold` in `evals.json`, overridable with the
`INTELLIGENTLY_Evals__PassRateThreshold` environment variable). The rubric checks legal transitions,
one evidence row per stage and model call, token and cost totals that match the rows, policy decisions
on every tool call, and outcome-specific rules. To add a fixture, write a JSON file in the same shape as
the existing ones; every file in the folder is loaded. No export tool exists yet.

---

## 7. Troubleshooting

| Symptom | Cause and action |
|---|---|
| Nobody can sign in on a new database | There is no default account. Create one with `dotnet run --project src/Intelligently.Tools -- create-user ...` (section 1.4). |
| Sign-in fails for a known user | Check the name with `list-users`. A user with a lockout end has failed 5 times; `reset-password --username X` sets a new password and clears the lockout. |
| Admin returns Access Denied | The account is not in the `Instructor` role. Run `add-instructor --username X`, then sign out and in again. |
| Demo form says "You are not enrolled in course N", or `POST /agent/runs` returns 403 | The user has no `intelligently:course` claim for that course. Run `grant-course --username X --course N`, then sign out and in again. |
| Account tool says `ConnectionStrings:DefaultConnection is not set` | The environment is not Development (no LocalDB default) and no connection string is configured. Set `ConnectionStrings__DefaultConnection` or run through `dotnet run`, which uses the Development profile. |
| Account tool fails with "No password" in a script | Stdin is redirected, so the tool will not prompt. Set `INTELLIGENTLY_NEW_PASSWORD` for that command. |
| Run fails with "Anthropic:ApiKey not configured" | Set the key as a user secret or environment variable. A run that failed this way stays Running and is re-enqueued on every start until the key exists. |
| Approved run does not resume | Admin never steps runs. A Web host must be running with `ApprovalSweepSeconds` above 0; allow that many seconds. |
| Run stuck after a crash | The startup sweep resumes it, or another worker takes it over once the lease expires (600 s by default). |
| Lab runs always escalate | Docker is not reachable or the image could not be pulled. The tool returns an error, Verify fails the step, and the run retries, replans, then escalates. Check `Agents:Sandbox:DockerEndpoint` and that Docker Desktop is running. |
| First Docker pull fails with EOF | A transient proxy error seen once during verification; retry. |
| Warning "repeated prompt prefix ... read zero cache tokens" | Something volatile entered the cached prefix. Compare the prefix hash on consecutive model calls in the trace viewer and check system blocks and the tool list are stable. |
| Candidate search still on after clearing `TaskTypes` | Arrays merge by index. Set `Candidates` to 1 instead. |
| Model call ends with a refusal | The engine raises an exception, the run fails with the reason, and nothing retries. Check the reason in the trace viewer. |
| Cost shows zero for a model | Add the model under `Agents:Pricing:Models`. |
| Knowledge lookup matches poorly | The server has no full-text index, so matching uses `LIKE`. Install full-text search and reapply the Phase 3 migration on a fresh database. |

Known gaps as of the last verification:

- No code path has been exercised against the live Anthropic API. The procedure for a first live run is
  in `verification-sql-server-and-live-api.md`.
- The full-text catalog has never been created, because LocalDB lacks full-text search.
- The WebAssembly sandbox epoch timer runs on the thread pool, so a starved host could let a module run
  past its wall-clock limit until its fuel runs out.
- `notify_learner` records an event but delivers nothing.
