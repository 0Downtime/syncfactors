# SyncFactors.Next

`SyncFactors.Next` is a greenfield rewrite target for SyncFactors.

This folder is intentionally separate from the current PowerShell + Node implementation so the new design can be explored without destabilizing the existing app.

## Goals

- Consolidate the runtime into a single modern .NET stack.
- Keep the product local-first and operator-friendly.
- Preserve dry-run, review, approvals, rollback, and auditability as first-class capabilities.
- Replace shell-driven orchestration with typed domain services and explicit job/state models.
- Create a path from a single-tenant Windows admin tool to a future hosted control plane if needed.

## Proposed Stack

- Backend/API: ASP.NET Core
- Background execution: .NET hosted services
- UI: Razor Pages first, with light progressive enhancement
- Data: SQLite
- Directory integration: .NET + PowerShell seam only where necessary
- Tests: xUnit + approval/fixture-style scenario tests

## Solution Shape

- `src/SyncFactors.Api`: local operator UI and HTTP API
- `src/SyncFactors.Worker`: background sync execution host
- `src/SyncFactors.Domain`: core lifecycle rules and orchestration contracts
- `src/SyncFactors.Infrastructure`: SQLite, AD, SuccessFactors, email, filesystem, process adapters
- `src/SyncFactors.Contracts`: shared DTOs/events
- `tests/*`: unit and integration test projects
- `docs/architecture.md`: target architecture
- `docs/migration-plan.md`: phased migration plan from the current repo

## Suggested First Milestones

1. Lock the domain model and storage model.
2. Implement read-only status/report browsing from SQLite.
3. Implement a dry-run worker preview flow end to end.
4. Implement delta/full sync execution.
5. Add approvals, rollback, and operator actions.

## Status

This folder now builds against the locally installed .NET 10 SDK. The implementation is still intentionally minimal and only covers the first read-model scaffold.
