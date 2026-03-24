# Target Architecture

## Why Rewrite This Way

The current system has strong domain coverage, but the runtime is split across:

- PowerShell for sync orchestration and AD operations
- Node/Express for the local API
- React for the dashboard

That split introduces avoidable friction:

- the web layer shells out to PowerShell for core actions and status
- runtime state is procedural rather than strongly modeled
- test coverage is deep in PowerShell, but the future product direction points toward a longer-lived application runtime

The rewrite should optimize for operational safety, Windows/AD interoperability, and a future path to a richer control plane.

## Proposed Runtime

### 1. SyncFactors.Api

Responsibilities:

- Serve the local operator UI
- Expose HTTP endpoints for status, reports, worker detail, approvals, and operator actions
- Subscribe to worker/job updates
- Enforce local auth and role boundaries if needed later

Implementation notes:

- ASP.NET Core
- Razor Pages or MVC views first
- Server-sent events or SignalR for live status updates

### 2. SyncFactors.Worker

Responsibilities:

- Run sync jobs
- Execute scheduled and ad hoc work
- Own state transitions for jobs and worker actions
- Publish progress and persist journals

Implementation notes:

- .NET Worker Service
- Queue abstraction for `delta`, `full`, `review`, and `worker preview`
- Single active run by default for safety

### 3. SyncFactors.Domain

Responsibilities:

- Pure business logic
- Worker lifecycle policy
- Matching and identity resolution
- Attribute diffing
- Guardrail evaluation
- Approval policy
- Rollback planning

Guidelines:

- No filesystem
- No HTTP
- No PowerShell execution
- No SQLite concerns

### 4. SyncFactors.Infrastructure

Responsibilities:

- SuccessFactors client
- Active Directory gateway
- SQLite persistence
- filesystem/report output
- email/alert transport
- optional PowerShell compatibility adapters during migration

Guidelines:

- Treat external systems as ports/adapters
- Keep providers narrow and testable

### 5. SyncFactors.Contracts

Responsibilities:

- DTOs shared by API, worker, and UI
- event payloads
- status/report shapes

## Core Domain Model

Primary concepts:

- `SyncJob`
- `WorkerSnapshot`
- `DirectoryUserSnapshot`
- `WorkerMatch`
- `AttributeDiff`
- `PlannedAction`
- `ApprovalRequirement`
- `ReviewCase`
- `RollbackOperation`
- `RunJournal`
- `RuntimeStatus`

Representative domain services:

- `IWorkerSource`
- `IDirectoryGateway`
- `IIdentityMatcher`
- `IAttributeMapper`
- `ILifecyclePolicy`
- `IGuardrailPolicy`
- `IApprovalPolicy`
- `IRollbackPlanner`
- `IRunRepository`
- `IRuntimeStatusStore`

## Storage Model

SQLite remains the right default for local-first operation.

Suggested tables:

- `runs`
- `run_events`
- `run_entries`
- `worker_snapshots`
- `review_cases`
- `approvals`
- `rollback_operations`
- `runtime_status`
- `config_versions`

Design rules:

- append-only events for auditability
- materialized read tables for dashboard queries
- explicit schema versioning from day one

## UI Direction

Use server-rendered pages first.

That fits the actual workflow:

- inspect current run
- triage queues
- inspect worker history
- launch dry-run or real-run actions
- review artifacts and approvals

This avoids rebuilding a heavy client-side state machine before the new domain runtime is stable.

## Integration Boundaries

### SuccessFactors

- move from ad hoc script-driven queries to a typed client
- preserve config-driven field selection and preview-query behavior
- keep raw payload capture available for debugging

### Active Directory

- use native .NET directory APIs where they are sufficient
- keep a PowerShell fallback adapter for operations that are materially easier or safer through existing cmdlets during migration
- isolate this behind one `IDirectoryGateway`

### Reporting

- keep machine-readable JSON artifacts
- add normalized event records instead of deriving everything from loose report files

## Non-Goals for the First Rewrite Slice

- multi-tenant SaaS hosting
- distributed workers
- config GUI editor
- plugin marketplace

Those should remain future options, not early constraints.
