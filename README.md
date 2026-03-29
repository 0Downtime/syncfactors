# [Alpha] SyncFactors.Next

`SyncFactors.Next` is the primary SyncFactors implementation and repository root.

The legacy PowerShell + Node implementation now lives in `SyncFactors.Old/` for maintenance and migration reference.

> [!WARNING]
> [Alpha] This software is in active development, has a high risk of failure, and is not ready for production use.
> Expect breaking changes, incomplete workflows, missing features, and operational defects. Validate everything in a non-production environment first.

## Current State

- The repository root is now the .NET-based `SyncFactors.Next` implementation.
- The legacy PowerShell application has moved under `SyncFactors.Old/`.
- The current stack is local-first and centered on ASP.NET Core, background workers, and SQLite-backed runtime state.
- Production readiness is not implied by the current feature set, repository layout, or available scripts.

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
- `src/SyncFactors.Contracts`: shared DTOs and events
- `tests/*`: unit and integration test projects
- `docs/architecture.md`: target architecture
- `docs/migration-plan.md`: phased migration plan from the legacy implementation
- `config/*`: tracked sample config, local config, and scaffold configuration

## Status

The solution now builds from the repository root against the locally installed .NET 10 SDK.

This repository is still in alpha. Design direction is clearer than operational maturity. Expect APIs, config shapes, workflows, and storage details to change while the rewrite settles.

## Local Config

The rewrite keeps its tracked and local config files under `config`. Use the `sample.*.json` files there as templates and keep machine-specific values in the ignored `local.*.json` files in the same folder.

For Active Directory binds, the current .NET LDAP integration uses simple bind semantics. Set `SF_AD_SYNC_AD_USERNAME` to a UPN such as `svc_successfactors@example.local`, not a down-level logon name such as `EXAMPLE\svc_successfactors`, or AD may reject the credentials even when the password is correct.

## Mock SuccessFactors

Use `src/SyncFactors.MockSuccessFactors` to run a local SuccessFactors-like API for development.

- Preferred local launcher: `scripts/Start-SyncFactorsMockSuccessFactors.ps1`
- Start the mock server with `DOTNET_CLI_HOME=/tmp dotnet run --project src/SyncFactors.MockSuccessFactors`
- Point the sync config at `http://127.0.0.1:18080/odata/v2` using `config/sample.mock-successfactors.real-ad.sync-config.json`
- Baseline fixture data lives in `config/mock-successfactors/baseline-fixtures.json`
- Sample import data for sanitization lives in `config/mock-successfactors/sample-export.json`

Generate sanitized fixtures from exported OData payloads with:

```bash
DOTNET_CLI_HOME=/tmp dotnet run --project src/SyncFactors.MockSuccessFactors -- \
  generate-fixtures \
  --input config/mock-successfactors/sample-export.json \
  --output /tmp/sanitized-fixtures.json \
  --manifest /tmp/sanitized-fixtures.manifest.json
```

The mock intentionally supports only the current SyncFactors query shapes: OAuth or Basic auth, `PerPerson` for preview, `EmpJob` for the main worker query, `$format=json`, `$filter` on `personIdExternal` or `userId`, plus the existing `$select` and `$expand` paths used by the client.

If you need to capture a real `PerPerson` payload before sanitizing it, use:

- `scripts/Export-SfPerPerson.ps1` for OAuth client-credentials auth
- `scripts/Export-SfPerPerson-Basic.ps1` for Basic auth

If you need an admin-safe handoff file, use `scripts/Export-SfPerPerson-Sanitized.ps1`. It fetches the response, sanitizes it in memory, and writes only the sanitized JSON to disk. Add `-AliasOrgValues` if company, department, or location labels should also be anonymized, and `-KeepPersonIdExternal` if you need to preserve the source worker ID.

If your tenant rejects one of the optional fields in the hard-coded query, all three export scripts now support `-ExcludeSelectPath` and `-ExcludeExpandPath`. For example, to skip business unit:

```powershell
-ExcludeSelectPath "employmentNav/jobInfoNav/businessUnitNav/businessUnit" `
-ExcludeExpandPath "employmentNav/jobInfoNav/businessUnitNav"
```

If you want to probe the broader employee header set for one worker without sanitizing the output, use `scripts/Export-SfEmployeeHeaderProfile.ps1`. It wraps the same auto-retrying query logic with `-IncludeHeaderProfile -SkipSanitization` already enabled so you can see which fields your tenant actually exposes.

If you want to discover likely field mappings from tenant metadata before querying a worker, use `scripts/Get-SfMetadataFieldCandidates.ps1`. It downloads `/$metadata`, searches for the employee headers we discussed, and writes candidate entities and OData paths to JSON.

If you want to query one worker using the tighter, metadata-derived employee field set, use `scripts/Export-SfEmployeeMetadataProfile.ps1`. It targets the strongest candidate paths from the metadata analysis, skips sanitization, and keeps the same auto-retry behavior for unsupported tenant fields.
