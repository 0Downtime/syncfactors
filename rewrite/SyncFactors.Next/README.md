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
- `config/*`: rewrite-local sync, mapping, and scaffold configuration

## Suggested First Milestones

1. Lock the domain model and storage model.
2. Implement read-only status/report browsing from SQLite.
3. Implement a dry-run worker preview flow end to end.
4. Implement delta/full sync execution.
5. Add approvals, rollback, and operator actions.

## Status

This folder now builds against the locally installed .NET 10 SDK. The implementation is still intentionally minimal and only covers the first read-model scaffold.

## Local Config

The rewrite now keeps its tracked and local config files under [`config`]( /Users/chrisbrien/dev/github.com/syncfactors/rewrite/SyncFactors.Next/config ). Use the `sample.*.json` files there as templates and keep machine-specific values in the ignored `local.*.json` files in the same folder.

## Mock SuccessFactors

Use [`src/SyncFactors.MockSuccessFactors`]( /Users/chrisbrien/dev/github.com/syncfactors/rewrite/SyncFactors.Next/src/SyncFactors.MockSuccessFactors ) to run a local SuccessFactors-like API for development.

- Start the mock server with `DOTNET_CLI_HOME=/tmp dotnet run --project src/SyncFactors.MockSuccessFactors`
- Point the sync config at `http://127.0.0.1:18080/odata/v2` using [`config/sample.mock-successfactors.real-ad.sync-config.json`]( /Users/chrisbrien/dev/github.com/syncfactors/rewrite/SyncFactors.Next/config/sample.mock-successfactors.real-ad.sync-config.json )
- Baseline fixture data lives in [`config/mock-successfactors/baseline-fixtures.json`]( /Users/chrisbrien/dev/github.com/syncfactors/rewrite/SyncFactors.Next/config/mock-successfactors/baseline-fixtures.json )
- Sample import data for sanitization lives in [`config/mock-successfactors/sample-export.json`]( /Users/chrisbrien/dev/github.com/syncfactors/rewrite/SyncFactors.Next/config/mock-successfactors/sample-export.json )

Generate sanitized fixtures from exported OData payloads with:

```bash
DOTNET_CLI_HOME=/tmp dotnet run --project src/SyncFactors.MockSuccessFactors -- \
  generate-fixtures \
  --input config/mock-successfactors/sample-export.json \
  --output /tmp/sanitized-fixtures.json \
  --manifest /tmp/sanitized-fixtures.manifest.json
```

The mock intentionally supports only the current SyncFactors query shape: OAuth or Basic auth, `PerPerson`, `$format=json`, `$filter` on `personIdExternal`, plus the existing `$select` and `$expand` paths used by the client.

If you need to capture a real `PerPerson` payload before sanitizing it, use:

- [`scripts/Export-SfPerPerson.ps1`](/Users/chrisbrien/dev/github.com/syncfactors/rewrite/SyncFactors.Next/scripts/Export-SfPerPerson.ps1) for OAuth client-credentials auth
- [`scripts/Export-SfPerPerson-Basic.ps1`](/Users/chrisbrien/dev/github.com/syncfactors/rewrite/SyncFactors.Next/scripts/Export-SfPerPerson-Basic.ps1) for Basic auth
