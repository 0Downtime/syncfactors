# High-Risk Hotspot Decomposition Plan

> **For Hermes:** Execute one slice at a time using strict TDD. Do not combine extraction, behavior changes, or cross-hotspot cleanup in one pull request.

**Goal:** Reduce the six oversized delivery hotspots into independently testable units without changing synchronization, Active Directory, SQLite, CLI, Razor, or dashboard behavior.

**Architecture:** Preserve each current public entry point and dependency-registration contract. Introduce internal collaborators behind that entry point first, move one cohesive responsibility at a time, and use the existing focused tests as behavioral contracts. The required extraction order puts persistence and protocol boundaries ahead of UI orchestration so UI work does not need to compensate for changing backend semantics.

**Tech stack:** .NET 10, xUnit, Microsoft.Data.Sqlite, System.DirectoryServices.Protocols, ASP.NET Core Razor Pages, ES modules, Vitest.

## Guardrails and non-goals

- This plan is intentionally not a rewrite and does not prescribe namespace-wide cleanup, renaming, or new abstractions unless a listed slice requires one.
- Preserve the public contracts registered by `src/SyncFactors.Api/Program.cs` and `src/SyncFactors.Worker/Program.cs`: `IDirectoryCommandGateway`, `IWorkerSource`, and `IRunRepository`.
- Preserve live-write protections, preview freshness behavior, identity conflict diagnostics, audit behavior, and SQLite migration/transaction behavior. Each must remain covered by the existing focused tests before and after a move.
- Do not touch worker log retention, `SYNCFACTORS_LOCAL_LOG_RETENTION_DAYS`, process/run/preview log cleanup, Windows service retention defaults, retention documentation, or retention tests. Those completed changes are outside this plan.
- Do not alter generated `src/SyncFactors.Api/wwwroot/dist/dashboard.js` or its map directly. Rebuild only when a future dashboard source change is intentionally merged.

## Characterization baseline

The existing suite already protects the five mature seams below. This card adds the missing top-level `AutomationCli` error-boundary characterization in `tests/SyncFactors.Automation.Tests/AutomationCliTests.cs`.

| Hotspot | Current boundary | Characterization suite to preserve |
| --- | --- | --- |
| `ActiveDirectoryCommandGateway` | `IDirectoryCommandGateway.ExecuteAsync` | `tests/SyncFactors.Infrastructure.Tests/ActiveDirectoryCommandGatewayTests.cs` |
| `SuccessFactorsWorkerSource` | `IWorkerSource.GetWorkerAsync` | `tests/SyncFactors.MockSuccessFactors.Tests/SuccessFactorsWorkerSourceIntegrationTests.cs` and `tests/SyncFactors.Infrastructure.Tests/SuccessFactorsWorkerSourcePreviewQueryTests.cs` |
| `SqliteRunRepository` | `IRunRepository` | `tests/SyncFactors.Infrastructure.Tests/SqliteRunRepositoryTests.cs` |
| `AutomationCli` | `AutomationCli.RunAsync` exit-code/output contract | `tests/SyncFactors.Automation.Tests/AutomationCliTests.cs`, `AutomationScenarioLoaderTests.cs` |
| `Runs/Detail` | Razor handlers and `DetailModel` display/export projections | `tests/SyncFactors.Api.Tests/RunDetailModelTests.cs` |
| `dashboard.entry.js` | pure dashboard helpers plus real-time lifecycle | `src/SyncFactors.Api/frontend/dashboard-axis.test.js`, `dashboard-runtime.test.js` |

Run the relevant command before starting a slice and again after each extraction. A failure discovered during an extraction must become a minimal regression test before production code changes.

## Sequencing and ownership

1. **Data and protocol boundaries:** `SqliteRunRepository`, then `SuccessFactorsWorkerSource`, then `ActiveDirectoryCommandGateway`. These change the foundation that API, Worker, and UI consume.
2. **Operator-facing orchestrators:** `Runs/Detail`, then `dashboard.entry.js`. These may consume the already-stable data projections but must not change them.
3. **Automation harness:** `AutomationCli` last. It should assemble existing collaborators rather than take ownership of scenario parsing, policy, reporting, or HTTP execution.
4. **One owner per hotspot:** the implementing card owns its source file(s), its focused test(s), and only its directly extracted collaborators. Another card may consume the public seam only after the owner commits its slice.
5. **Do not parallelize adjacent slices within one hotspot.** Parallel work is safe only across independent hotspots after their common contract tests are green.

## Task 1: Split SQLite read projections from command persistence

**Objective:** Move read-only run/list/detail/query mapping out of `SqliteRunRepository` while keeping the repository interface, SQL result ordering, and transaction semantics unchanged.

**Files:**
- Modify: `src/SyncFactors.Infrastructure/SqliteRunRepository.cs`
- Create: `src/SyncFactors.Infrastructure/SqliteRunReadRepository.cs` (or an internal query collaborator)
- Test: `tests/SyncFactors.Infrastructure.Tests/SqliteRunRepositoryTests.cs`

**Safe boundary:** Keep `SqliteRunRepository : IRunRepository` as the public adapter. Extract only `ListRunsAsync`, `GetRunAsync`, run-entry read/list/count projection code, row mapping, bucket ordering/labels, and report parsing into an internal read collaborator. Keep write methods, schema assumptions, connection creation, and active-run/queue mutation paths in the original class for this first slice.

**TDD sequence:**
1. Add one failing test for the exact read result affected by the intended move (for example, deterministic recent-run ordering or run-detail bucket backfill).
2. Run `dotnet test tests/SyncFactors.Infrastructure.Tests/SyncFactors.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~SqliteRunRepositoryTests --logger 'console;verbosity=minimal'` and observe the expected failure.
3. Extract the smallest read collaborator required to make the test pass; do not alter SQL, command text, or interface signatures in the same change.
4. Re-run the focused test project and `dotnet test SyncFactors.Next.sln --no-restore --logger 'console;verbosity=minimal'`.

**Stop condition:** Defer splitting writes, maintenance/vacuum, worker heartbeat, and schema migration logic until the read collaborator has a stable dedicated contract.

## Task 2: Split SuccessFactors request execution from worker projection

**Objective:** Isolate HTTP request construction/transport/response capture from JSON worker parsing, normalization, preview fallback, and enrichment without changing `IWorkerSource.GetWorkerAsync` results.

**Files:**
- Modify: `src/SyncFactors.Infrastructure/SuccessFactorsWorkerSource.cs`
- Create: `src/SyncFactors.Infrastructure/SuccessFactorsWorkerClient.cs`
- Create: `src/SyncFactors.Infrastructure/SuccessFactorsWorkerParser.cs`
- Test: `tests/SyncFactors.MockSuccessFactors.Tests/SuccessFactorsWorkerSourceIntegrationTests.cs`
- Test: `tests/SyncFactors.Infrastructure.Tests/SuccessFactorsWorkerSourcePreviewQueryTests.cs`

**Safe boundary:** Retain `SuccessFactorsWorkerSource` as the DI-resolved `IWorkerSource` adapter. First extract a client that returns status, content type, request URI, and raw response body; then extract parsing/identity normalization only after the transport boundary is green. Keep fallback-to-scaffold and delta/enrichment orchestration in `SuccessFactorsWorkerSource` until both extracted units are independently characterized.

**TDD sequence:**
1. Add a failing integration characterization for one transport outcome already supported by the source (successful primary lookup, preview-only lookup, or malformed JSON diagnostic).
2. Run the two focused suites above and observe the new test fail for the intended contract gap.
3. Move only the client or parser responsibility needed to pass.
4. Re-run both focused suites, then `dotnet test SyncFactors.Next.sln --no-restore --logger 'console;verbosity=minimal'`.

**Stop condition:** Do not combine OData query-shape changes, new SuccessFactors fields, retry-policy changes, or fallback policy changes with extraction.

## Task 3: Split Active Directory command planning from LDAP transport

**Objective:** Separate pure command-to-LDAP-request construction and diagnostic formatting from connection leasing, timeout/retry, and LDAP execution while preserving write ordering and non-retry-after-write guarantees.

**Files:**
- Modify: `src/SyncFactors.Infrastructure/ActiveDirectoryCommandGateway.cs`
- Create: `src/SyncFactors.Infrastructure/ActiveDirectoryCommandPlanner.cs`
- Create: `src/SyncFactors.Infrastructure/ActiveDirectoryCommandDiagnostics.cs`
- Test: `tests/SyncFactors.Infrastructure.Tests/ActiveDirectoryCommandGatewayTests.cs`

**Safe boundary:** Keep `ActiveDirectoryCommandGateway : IDirectoryCommandGateway` as the sole executable LDAP adapter and retain `ExecuteAsync`, leasing, timeout, `AsyncLocal` command context, invalidation, and retry decisions there. Extract pure request creation for create/update/group/graveyard operations first; extract diagnostic strings second. Never move a write behind a new retry layer without preserving the current `MarkWriteAttempted` contract.

**TDD sequence:**
1. Add a failing test for a single planner result or diagnostic payload that currently uses reflection only because no collaborator exists.
2. Run `dotnet test tests/SyncFactors.Infrastructure.Tests/SyncFactors.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~ActiveDirectoryCommandGatewayTests --logger 'console;verbosity=minimal'` and observe failure.
3. Extract one pure planner/diagnostic collaborator with no LDAP connection dependency.
4. Re-run the focused suite and `dotnet test SyncFactors.Next.sln --no-restore --logger 'console;verbosity=minimal'`.

**Stop condition:** Do not split connection pooling/factory behavior or introduce concurrent write changes in this slice.

## Task 4: Split run-detail query/format/export projections

**Objective:** Reduce `DetailModel` without changing Razor route binding, page selection/filtering, text labels, or JSON/JSONL/CSV export bytes.

**Files:**
- Modify: `src/SyncFactors.Api/Pages/Runs/Detail.cshtml.cs`
- Create: `src/SyncFactors.Api/Pages/Runs/RunDetailDisplayService.cs`
- Create: `src/SyncFactors.Api/Pages/Runs/RunEntryExportService.cs`
- Test: `tests/SyncFactors.Api.Tests/RunDetailModelTests.cs`
- Preserve: `src/SyncFactors.Api/Pages/Runs/Detail.cshtml`

**Safe boundary:** Keep `DetailModel` as the Razor `PageModel` and keep all `[BindProperty]` names and handler signatures. Extract pure display calculations first (run context, diagnostic sections, execution labels, snapshots) and export serialization second. Make exported filenames, content types, record order, and fields explicit tests before moving code.

**TDD sequence:**
1. Add a failing assertion for one existing handler’s serialized/export or display contract.
2. Run `dotnet test tests/SyncFactors.Api.Tests/SyncFactors.Api.Tests.csproj --no-restore --filter FullyQualifiedName~RunDetailModelTests --logger 'console;verbosity=minimal'` and observe failure.
3. Move the smallest pure display/export unit needed to pass without changing `Detail.cshtml`.
4. Re-run the focused project and `dotnet test SyncFactors.Next.sln --no-restore --logger 'console;verbosity=minimal'`.

**Stop condition:** Do not merge markup redesign, query parameter renames, endpoint changes, or CSS changes into this work.

## Task 5: Split dashboard data transformation from browser wiring

**Objective:** Turn pure dashboard rendering decisions into imported ES modules, leaving `dashboard.entry.js` as DOM selection, event binding, fetch/SignalR wiring, and lifecycle startup/shutdown.

**Files:**
- Modify: `src/SyncFactors.Api/frontend/dashboard.entry.js`
- Create: `src/SyncFactors.Api/frontend/dashboard-formatters.js`
- Create: `src/SyncFactors.Api/frontend/dashboard-runs.js`
- Create: `src/SyncFactors.Api/frontend/dashboard-timeline.js`
- Test: `src/SyncFactors.Api/frontend/dashboard-formatters.test.js`
- Test: `src/SyncFactors.Api/frontend/dashboard-runs.test.js`
- Preserve: `src/SyncFactors.Api/frontend/dashboard-axis.js`, `dashboard-runtime.js`, and their tests
- Generated after source approval only: `src/SyncFactors.Api/wwwroot/dist/dashboard.js`, `dashboard.js.map`

**Safe boundary:** Keep the entry-file IIFE, selectors, API URLs, SignalR event names, polling cadence, and `beforeunload` disposal behavior intact. Extract only input/output functions that accept snapshots/runs and return text, state, paging, timeline, or chart data; do not introduce a DOM test framework just to split pure functions.

**TDD sequence:**
1. Add a failing Vitest for one pure calculation (pagination clamp, filtered-run selection, timeline step, or status formatting).
2. Run `cd src/SyncFactors.Api && npm run test:ui` and observe the intended failure.
3. Extract one pure module and import it from `dashboard.entry.js`.
4. Run `npm run test:ui` and `npm run build:ui`; inspect the generated diff and include generated files only when it is solely the expected build output.

**Stop condition:** Do not change browser-visible wording, animation policy, fetch URLs, SignalR subscriptions, or lifecycle semantics during extraction.

## Task 6: Make AutomationCli an orchestration-only entry point

**Objective:** Keep `AutomationCli.RunAsync` as the exit-code and console boundary while leaving options parsing, scenario loading, risk policy, runner execution, report writing, and failure analysis in dedicated collaborators.

**Files:**
- Modify: `src/SyncFactors.Automation/AutomationCli.cs`
- Test: `tests/SyncFactors.Automation.Tests/AutomationCliTests.cs`
- Test: `tests/SyncFactors.Automation.Tests/AutomationScenarioLoaderTests.cs`

**Safe boundary:** `RunAsync` owns only orchestration, user-facing console output, and translating an exception into exit code `1`. Do not change option precedence, report locations, scenario risk policy, certificate validation, or HTTP request behavior while reducing its size. Extract a collaborator only if it has a concrete constructor input/output and can receive a direct test.

**TDD sequence:**
1. Add a failing top-level CLI test for a changed orchestration path (invalid argument, no matched scenarios, risk policy rejection, or report failure).
2. Run `dotnet test tests/SyncFactors.Automation.Tests/SyncFactors.Automation.Tests.csproj --no-restore --filter FullyQualifiedName~AutomationCliTests --logger 'console;verbosity=minimal'` and observe failure.
3. Make the smallest extraction that preserves exit code and user-facing output.
4. Re-run the focused project and `dotnet test SyncFactors.Next.sln --no-restore --logger 'console;verbosity=minimal'`.

**Stop condition:** Do not alter production automation targets or run destructive scenarios as part of refactoring verification.

## Completion criteria for each future slice

- A new or moved behavior has a focused test that failed before the minimal implementation change.
- The focused hotspot suite and the solution suite pass.
- `git diff --check` passes.
- DI registration and public interfaces remain unchanged unless the card explicitly owns and tests a contract migration.
- No excluded worker-log retention file or behavior is modified.
- A separate reviewer verifies the diff before commit; generated browser artifacts are rebuilt and reviewed only when dashboard source changed.
