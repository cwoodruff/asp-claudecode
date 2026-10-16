# Verification against real infrastructure: SQL Server and the live Anthropic API

Date: 2026-10-16. Branch `claude/priceless-perlman-1b897f` on top of `566956d` (PR #5 merge).
Machine: Windows 11, .NET SDK 11.0.100-rc.1, EF Core tools 10.0.5, Docker Desktop 4.93 (engine 29.8.1),
SQL Server 2025 Express LocalDB and a SQL Server 2025 Developer instance (`MSSQLSERVER`).

Every implementation phase (0–6) was tested only on SQLite in-memory test hosts with a mocked
`IReasoningEngine`. This note records what happened when the runtime met a real SQL Server, a real Docker
engine and the real sandbox image, and what still has to happen before the live-API smoke run can be called
done.

## Summary

| Item | Result |
|---|---|
| 1. Apply all migrations to SQL Server | **Passed.** All six migrations applied to LocalDB; schema matches the model; nothing needed fixing. |
| 2. Live API smoke run (one `LabErrorDiagnosis` run) | **Not run: `Anthropic:ApiKey` is not configured anywhere on this machine.** Everything else is in place (database, accounts, sandbox image and command, host boots and signs in). |
| 3. Fixes that blocked 1 and 2 | Three: the C# sandbox command could not build offline (restore failure, a real blocker for the pilot task), plus two configuration gaps (user-secrets id, Development connection string). No migration, query or SDK call needed a change. |
| 4. Wasm epoch timing test | **Reproduced (failed 2 of 3 runs) and made deterministic**; three consecutive full unit runs green afterwards. |
| Test suite | 250 unit + 43 integration passing. |

## 1. SQL Server migrations

### Which database

No `ConnectionStrings:DefaultConnection` is configured by the user: `appsettings.json` and
`appsettings.Development.json` have none, there is no user-secrets store on the machine (and neither host
had a `UserSecretsId`, so one could not have been read), and no process, user or machine environment
variable sets it. The repository's own `DesignTimeDbContextFactory` falls back to
`Server=(localdb)\MSSQLLocalDB;Database=Intelligently` when the `ConnectionStrings__DefaultConnection`
variable is absent, and LocalDB answered, so the migrations were applied there. That is the checked-in
default for `dotnet ef`, not an invented connection string, and the database is a local throwaway.

The full `MSSQLSERVER` Developer instance on `localhost` (full-text search installed) was reachable but was
not touched, because nothing configures it. It is the place to exercise the one branch LocalDB cannot (see
below).

### What ran

```bash
dotnet ef database update --project src/Intelligently.Infrastructure --startup-project src/Intelligently.Infrastructure
```

Applied in order, each without error: `InitialSchema`, `Phase2ControlPlane`, `Phase3ContextPlane`,
`AddRunLease`, `Phase5EvidencePlane`, `Phase6LabErrorDiagnosis`. `dotnet ef migrations script --idempotent`
generated 898 lines of T-SQL with no provider warnings, and
`dotnet ef migrations has-pending-model-changes` reports **no changes** between the model and the last
migration, so the snapshot generated during the SQLite-tested phases is the SQL Server model.

The only message during the update was EF's standard notice that the Phase 3 full-text block runs with
`suppressTransaction: true`; that is intended (full-text DDL cannot run inside the migration transaction).

### Points the task asked to watch

| Concern | What SQL Server has |
|---|---|
| Owned `RecoveryBudget` | Five non-null columns on `AgentRuns`: `Budget_ReplansLeft`, `Budget_RetriesLeft`, `Budget_ToolCallsLeft`, `Budget_TokensLeft` (int) and `Budget_WallClockTicksLeft` (bigint, the `TimeSpan` ticks conversion). The owned `RunPrincipal` landed the same way (`Principal_UserId` nvarchar(450), `Principal_RolesJson`, `Principal_CourseScopeJson`). |
| `AgentTrace` append-only | Enforced in `IntelligentlyDbContext.SaveChanges`, not in the schema, so nothing provider-specific to migrate. The Phase 5 columns are present: `CostUsd` decimal(12,6), `ReasoningSummary` nvarchar(max), `StopReason` nvarchar(50), `Effort` nvarchar(20), `LatencyMs` float; `Id` is a bigint identity. |
| `IdempotencyRecord` key | `AgentIdempotencyRecords` has a uniqueidentifier primary key and the unique index `IX_AgentIdempotencyRecords_RunId_ToolName_ArgsHash` (`ArgsHash` nvarchar(64)), cascade from `AgentRuns`. |
| Run lease columns | `ClaimedBy` nvarchar(200) null and `ClaimedUntil` datetime2 null on `AgentRuns`; `ConcurrencyStamp` is a uniqueidentifier. Both concurrency tokens are plain columns (EF compares them in the `WHERE` of the update), so no rowversion mapping was needed. |
| `Phase6LabErrorDiagnosis` | `AgentRuns.FinalExplanation` and `FixDiff` nvarchar(max) null; `AgentApprovals.RuleKind` nvarchar(30) and `Rule` nvarchar(100) null. |
| Full-text index (Phase 3) | `FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')` is 0 on LocalDB, so the guard took the no-op path and `sys.fulltext_indexes` is empty; `KnowledgeRetriever` will use its `LIKE` fallback here. **The `CREATE FULLTEXT CATALOG/INDEX` branch has still not been executed anywhere.** Running the same `database update` against `localhost` (MSSQLSERVER, full-text installed) would cover it. |

All twenty tables are present (Identity, the five AI tables, the seven agent tables and the migrations
history). The index list on the agent tables matches `AgentModel.Configure` one for one.

### Host on SQL Server

With the Development connection string added (section 3) `dotnet run --project src/Intelligently.Web`
starts against LocalDB: `RunWorker` starts and its startup sweep (`SELECT ... FROM AgentRuns WHERE Status =
N'Running'`) executes, and the OpenTelemetry console exporter prints the request spans. Two Identity
accounts were provisioned through `UserManager` (a learner enrolled in course 1 via the
`intelligently:course` claim, and an instructor in the `Instructor` role); signing in as the learner at
`/Account/Login` redirects to `/agent`, which shows "Running as learner1 · enrolled in course(s) 1". The
account names and generated passwords are in the side-session notes folder
(`verification-local-accounts.md`), outside the repository.

## 2. Live API smoke run: blocked on the API key

`Anthropic:ApiKey` is not set in `appsettings*.json`, in user secrets (no store existed) or in any
environment variable (`Anthropic__ApiKey`; only `ANTHROPIC_BASE_URL` is present). Both hosts throw
`Anthropic:ApiKey not configured` the first time the client is resolved, which for the Web host is the first
run's Plan stage. No run was created, because it would have failed there and stayed `Running` in the
database to be re-enqueued on every start.

Everything else the run needs is ready:

- Database and accounts as above. `CandidateSearch` stays on for `LabErrorDiagnosis` in
  `appsettings.Development.json`; **set `Agents:CandidateSearch:Candidates` to `1` for the smoke run** (user
  secrets or an `Agents__CandidateSearch__Candidates=1` override) so the run is a single planner call.
  `IsEnabledFor` requires more than one candidate, so that value turns the search off; overriding
  `TaskTypes` with an empty value does not, because configuration arrays merge by index and
  `TaskTypes:0` from the Development file survives.
- Docker Desktop is running and `mcr.microsoft.com/dotnet/sdk:10.0` (1.3 GB) is pulled. The first two pulls
  failed with an `EOF` from Docker Desktop's proxy to Microsoft's registry while Docker Hub pulls worked; the
  third attempt succeeded, so it was transient. The real `DockerSandbox` is the default `ISandbox`; nothing
  needs the fake. Its unit tests (`DockerSandboxTests`) ran against the live engine, none skipped.
- The sandbox command for `csharp` was run in a throwaway container with the sandbox's limits
  (`--network none`, 512 MB, 1 CPU, 256 pids). **As shipped it failed for every file**, including a correct
  one: `dotnet run Program.cs` on a .NET 10 file-based app defaults to `PublishAot=true`, which restores the
  ILCompiler package from nuget.org, and with no network the build stops at `NU1301` after about 19 s, before
  the lab is compiled. With `-p:PublishAot=false` (now the default template, section 3) the compile-error file
  reports `CS1003` with exit 1 in about 2 s and the fixed file prints the expected output with exit 0 in about
  4 s, both offline and well inside the 60 s sandbox timeout.
- The lab for the run: one learner file `Program.cs` with a missing semicolon, `error_text` the CS1003
  line, `expected_output` `"Hello, lab!\n"`, `learner_level` Beginner, course 1.

**The live API smoke run was not performed.** The decision was to close this verification without it rather
than wait for a key. To run it later:

1. Supply the key once for both hosts (they share the `UserSecretsId` added here), and turn the candidate
   search off so the run is one planner call:

   ```bash
   dotnet user-secrets set "Anthropic:ApiKey" "<key>" --project src/Intelligently.Web
   ```

   ```bash
   dotnet user-secrets set "Agents:CandidateSearch:Candidates" "1" --project src/Intelligently.Web
   ```

2. Start the Web app (`dotnet run --project src/Intelligently.Web`, Development profile, LocalDB) and
   sign in as the seeded learner (accounts in `verification-local-accounts.md` in the side-session notes
   folder, or create one with `dotnet run --project src/Intelligently.Tools -- create-user --username learner1
   --email learner1@example.edu --course 1`, see the user guide section 1.4).
3. Create one run as that learner with `POST /agent/runs`: `courseId` 1, `taskType` `LabErrorDiagnosis`, and
   the lab above as `taskSpec` (`language` `csharp`, `entrypoint` `Program.cs`, `course_files` empty).
4. Watch `GET /agent/runs/{id}` (htmx partial polls while Running) and, as the seeded instructor, the Admin
   trace viewer at `/runs/{id}`.
5. Record in this note:
   - whether structured-output planning succeeded (the Plan stage produced an `AgentPlan` with template
     phases; the Plan `ModelCall` row's stop reason);
   - whether the reasoning summary was captured (`ReasoningSummary` on the Plan `ModelCall` row is non-empty,
     from `thinking: {type: adaptive, display: summarized}`);
   - whether the prompt cache hit on the second model call of the run (`CacheReadInputTokens > 0` on the
     second `ModelCall` row; `CacheHealthMonitor` warns when a repeated prefix reads zero);
   - the total cost recorded (`AgentRun.TotalCostUsd` and the `AgentRunFinished` analytics event);
   - the final stage and status (`Completed` with `FinalExplanation` and `FixDiff` stored, or where it
     stopped and why).

## 3. What was fixed

- The default `csharp` command template in `AgentsOptions.SandboxOptions.Languages` is now
  `dotnet run {entrypoint} -p:PublishAot=false`. Without the property no C# lab can be built in the
  network-less sandbox (section 2), so `reproduce` would always be a tool error and the pilot task could
  never pass. Configuration that sets `Agents:Sandbox:Languages` explicitly must carry the same flag. The two
  tests that assert the expanded command were updated.

Configuration only:

- `UserSecretsId` (`intelligently-local`, shared) on `Intelligently.Web.csproj` and
  `Intelligently.Admin.csproj`. Without it neither host reads user secrets at all, so the key could not be
  configured the way the task expects.
- `ConnectionStrings:DefaultConnection` in both `appsettings.Development.json` files, set to the same LocalDB
  string the design-time factory already defaults to, so `dotnet run` in Development reaches the database
  the migrations were applied to. Production still requires the real value.

Not changed: no migration, no query and no SDK call failed, so the `claude-api` skill was not needed and
`AnthropicReasoningEngine` is untouched. The Docker sandbox code itself is unchanged; only the default argv
for one language moved.

## 4. The Wasm epoch timing test

`WasmSandboxTests.Epoch_deadline_stops_an_endless_loop_within_the_wall_clock_limit` was run three times in
the full unit suite before any change: pass, fail, fail. The failure was not the 5 s wall-clock margin but

```
Expected result.Error "The module exhausted its fuel budget." to contain "limit".
```

The sandbox advances Wasmtime's epoch from a `System.Threading.Timer` every 10 ms; under a fully parallel
test run the thread-pool callbacks lag, the 300 ms deadline arrives late, and the spin loop burns through the
default 2 000 000 000 fuel first. The run still stops within about a second and `TimedOut` is true, so the
sandbox is behaving; the test was asserting which of two limits fired.

Fix (test only): the test now runs with `fuel: long.MaxValue` so the epoch deadline is the only limit that can
stop the loop, asserts `ExitCode` is null as the sibling fuel test does, and uses a `Stopwatch` with a 20 s
margin instead of 5 s for the same thread-pool reason. The sandbox's own timeout in the test is still 300 ms.
After the change the full unit suite passed three consecutive times (250/250 each), and once more after the
sandbox template change together with the integration suite (43/43).

## Open items

1. **Live API smoke run** (section 2): not performed; needs `Anthropic:ApiKey`. Follow the five steps there
   and record the five answers in this note.
2. **Full-text branch of `Phase3ContextPlane`**: apply the migrations to a SQL Server with full-text
   installed (`localhost` on this machine) and run a `lookup_knowledge` call so `FREETEXT` executes once.
3. The Wasm epoch tick runs on the thread pool; if the lag seen in tests matters in production (a starved
   host could let a module run past its wall-clock limit until fuel runs out), a dedicated timer thread
   would make the deadline independent of pool pressure. Out of scope here as a behaviour change.
