# Migration Plan

## Intent

This plan assumes the current PowerShell implementation remains the production baseline while the new .NET stack is built in parallel.

## Phase 0: Freeze the Contract

Goal:

- document what the current system does before rewriting behavior

Tasks:

1. Inventory current operator-visible flows:
   - delta run
   - full run
   - review run
   - worker preview
   - worker apply
   - preflight
   - rollback
2. Capture the current data contracts:
   - runtime status JSON
   - run report JSON
   - queue/report entry shapes
3. Extract core behavioral rules from PowerShell tests into a rewrite checklist.

Exit criteria:

- every high-risk behavior has a named rule or scenario
- no rewrite work starts from assumptions

## Phase 1: Build the Read Model First

Goal:

- get the new stack reading operational state before it mutates anything

Tasks:

1. Create SQLite schema and repositories.
2. Implement read-only run history, queue, and worker detail endpoints.
3. Render a minimal operator dashboard in ASP.NET Core.
4. Mirror a subset of the current report pages.

Exit criteria:

- new UI can replace the existing read-only dashboard for status/history

## Phase 2: Port the Domain Core

Goal:

- move business rules into typed domain services

Tasks:

1. Port config loading and validation.
2. Port worker identity and matching rules.
3. Port attribute mapping and required field validation.
4. Port lifecycle decisions:
   - create
   - update
   - enable prehire
   - disable terminated
   - move to graveyard OU
   - delete after retention
5. Port guardrails and manual-review gating.

Exit criteria:

- dry-run planning in .NET matches current behavior for fixture scenarios

## Phase 3: Add External Adapters

Goal:

- connect the new planner to real systems safely

Tasks:

1. Implement typed SuccessFactors client.
2. Implement AD gateway.
3. Keep a temporary PowerShell-backed adapter where parity is faster than reimplementation.
4. Write integration fixtures for tenant query edge cases and AD mutation plans.

Exit criteria:

- preview and dry-run complete without using the current top-level scripts

## Phase 4: Replace Execution Flows

Goal:

- make the .NET worker the active runtime

Tasks:

1. Implement delta/full/review job execution.
2. Implement runtime status streaming.
3. Implement approvals and worker-scoped apply flows.
4. Implement rollback journal writing and replay.
5. Implement alerts.

Exit criteria:

- one pilot environment can run end to end on the new worker

## Phase 5: Decommission the Old Shell

Goal:

- remove the old runtime layer with low risk

Tasks:

1. Switch operator docs to the new app.
2. Retain PowerShell only for compatibility scripts where justified.
3. Archive old report adapters once parity is proven.
4. Remove duplicate web/API paths.

Exit criteria:

- PowerShell is no longer the primary orchestration runtime

## Cross-Cutting Tracks

### Testing

- Translate the highest-value Pester scenarios into xUnit scenario tests.
- Keep a parity matrix between old and new results.
- Treat lifecycle and safety rules as approval-test material.

### Config

- Support the current JSON config shape first.
- Add versioning rather than redesigning config immediately.

### Safety

- Default to dry-run and review-first behavior in early releases.
- Preserve explicit approval semantics for destructive actions.

## Suggested First 30 Tasks

1. Install the .NET SDK locally.
2. Create the solution and projects from the placeholder files in this folder.
3. Add nullable, analyzers, and formatting defaults.
4. Define contracts for `RuntimeStatus`, `RunSummary`, `RunEntry`, and `WorkerDetail`.
5. Define repositories for runs and entries.
6. Create SQLite migration `0001_initial`.
7. Build a read-only `/status` endpoint.
8. Build a read-only `/runs` endpoint.
9. Build a read-only `/runs/{id}` endpoint.
10. Build a read-only `/workers/{id}` endpoint.
11. Render a basic dashboard page.
12. Render a run detail page.
13. Render a worker detail page.
14. Define config POCOs that match the current JSON shape.
15. Implement config validation.
16. Define `WorkerSnapshot` and `DirectoryUserSnapshot`.
17. Port identity resolution rules.
18. Port status/start-date normalization rules.
19. Port attribute mapping logic.
20. Port diff generation.
21. Port guardrail checks.
22. Port review-case generation.
23. Implement a dry-run planner.
24. Implement a fake SuccessFactors client for tests.
25. Implement a fake AD gateway for tests.
26. Translate 10 high-value Pester scenarios.
27. Implement one preview-worker endpoint.
28. Add structured logging.
29. Add report artifact writing.
30. Demo a worker preview end to end from the new app.
