# [Alpha] SyncFactors

`SyncFactors` is the primary SyncFactors implementation and repository root.

> [!WARNING]
> [Alpha] This software is in active development, has a high risk of failure, and is not ready for production use.
> Expect breaking changes, incomplete workflows, missing features, and operational defects. Validate everything in a non-production environment first.

## Current State

- The repository root is now the .NET-based `SyncFactors` implementation.
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
- `config/*`: tracked sample config, local config, and scaffold configuration

## Codex Worktrees On macOS
Codex app worktrees can bootstrap this repository automatically through the checked-in local environment at [`.codex/environments/environment.toml`](/Users/chrisbrien/.codex/worktrees/be52/syncfactors/.codex/environments/environment.toml). Open the project in the Codex app, choose the local environment when starting a worktree thread, and Codex will run [`scripts/codex/setup-worktree-macos.sh`](/Users/chrisbrien/.codex/worktrees/be52/syncfactors/scripts/codex/setup-worktree-macos.sh) on worktree creation. That macOS wrapper now delegates to the shared PowerShell bootstrap script so the setup behavior matches Windows.

This setup is macOS-only in v1 and is intentionally scoped to the core local dev loop:
- prepare local config files for the .NET rewrite when missing
- copy ignored local runtime files from the primary checkout when missing
- fall back to tracked `config/sample*.json` files when local config files are still missing
- create runtime/report directories used by the .NET API and worker
- fall back to [`.env.worktree.example`](/Users/chrisbrien/.codex/worktrees/be52/syncfactors/.env.worktree.example) when `.env.worktree` is still missing

The per-worktree runtime contract is `.env.worktree`. Keep auth, profile selection, ports, and local overrides there rather than in tracked JSON or tracked `.codex` files. JSON remains the structural config layer. On Windows, `scripts/codex/Load-WorktreeEnv.ps1` now checks Windows Credential Manager first for each variable and falls back to `.env.worktree` when no stored value exists. The default local test path is fake SuccessFactors plus real Active Directory, so fill in the AD values there before starting the rewrite services. The default values are:

```bash
SYNCFACTORS_RUN_PROFILE=mock
SYNCFACTORS_CONFIG_PATH=
SYNCFACTORS_MAPPING_CONFIG_PATH=./config/local.syncfactors.mapping-config.json
SYNCFACTORS_SQLITE_PATH=state/runtime/syncfactors.db
SYNCFACTORS_API_PORT=5087
MOCK_SF_PORT=18080
```

On macOS, you can keep sensitive `SF_AD_SYNC_*` values out of `.env.worktree` entirely and store them in the login Keychain instead. The launchers now fall back to the Keychain service named by `SYNCFACTORS_KEYCHAIN_SERVICE` (default `syncfactors`) when those variables are blank in `.env.worktree`. To store one:

```bash
./scripts/codex/set-macos-keychain-secret.sh SF_AD_SYNC_AD_BIND_PASSWORD
```

The helper stores the secret under account name equal to the environment variable name, so blank values in `.env.worktree` will be filled automatically on macOS launches.

Set `SYNCFACTORS_RUN_PROFILE=mock` or `real` to switch the active SuccessFactors config. Leave `SYNCFACTORS_CONFIG_PATH` empty for profile-based resolution, or set it only when you want an explicit one-off override. In secondary worktrees, the setup script first seeds `.env.worktree` and root `config/local*` or `config/*.variables` files from the primary checkout when they exist. It then falls back to tracked sample files for anything still missing. Existing local files stay untouched, so it is safe to rerun. It prepares local files and directories only; it does not start long-running services. The primary launcher is PowerShell so the same commands work on macOS and Windows. On macOS, the Codex helper actions still open dedicated terminal windows through the local shell helper.

The intended local test loop is:
- run `pwsh ./scripts/codex/setup-worktree.ps1`
- fill in `.env.worktree` with your real AD credentials and any local overrides
- on Windows, import those values with `pwsh ./scripts/codex/Save-WorktreeEnvToWindowsCredentialManager.ps1`
- start one service with `pwsh ./scripts/codex/run.ps1 -Service mock|api|worker`
- or open the profile-aware local stack with `pwsh ./scripts/codex/run.ps1 -Service stack`
- use `-Profile real` to override the profile for a single run without editing `.env.worktree`

On Windows, the effective lookup order is:
- the worktree-scoped Windows Credential Manager entry for that variable
- the value in `.env.worktree`
- the fallback default from `.env.worktree.example` where one exists

Use `pwsh ./scripts/codex/Save-WorktreeEnvToWindowsCredentialManager.ps1 -RemoveEmptyValues` if you want blank entries in `.env.worktree` to delete the corresponding stored credentials instead of saving empty strings.

When you run `-Service stack`, the launched services depend on the active profile:
- `mock`: starts the mock SuccessFactors API, the SyncFactors .NET API, and the worker
- `real`: starts the SyncFactors .NET API and the worker

On macOS, `./scripts/codex/setup-worktree-macos.sh` remains available as a wrapper. On Windows, use the PowerShell commands directly.

For project-scoped `.codex` settings to load, this repo or one of its parent paths must be marked trusted in `~/.codex/config.toml`. Codex skips project-scoped `.codex` layers for untrusted projects.

## Status

The solution now builds from the repository root against the locally installed .NET 10 SDK.

This repository is still in alpha. Design direction is clearer than operational maturity. Expect APIs, config shapes, workflows, and storage details to change while the rewrite settles.

## Local Config

The rewrite keeps its tracked and local config files under `config`. Use the `sample.*.json` files there as templates and keep machine-specific values in the ignored `local.*.json` files in the same folder. The local JSON files are editable copies, not the primary environment selector. The active sync config is chosen from `.env.worktree` via `SYNCFACTORS_RUN_PROFILE` unless you explicitly override `SYNCFACTORS_CONFIG_PATH`.

For Active Directory binds, the current .NET LDAP integration uses simple bind semantics. Set `SF_AD_SYNC_AD_USERNAME` to a UPN such as `svc_successfactors@example.local`, not a down-level logon name such as `EXAMPLE\svc_successfactors`, or AD may reject the credentials even when the password is correct.

For full-sync `EmpJob` queries, `successFactors.query.inactiveRetentionDays` can extend the source filter to keep recently inactive workers in scope without hand-writing the date cutoff in `baseFilter`. With the default fields, a config like `"baseFilter": "emplStatus in 'A','U'"` plus `"inactiveRetentionDays": 180` expands to include terminated (`emplStatus eq 'T'`) workers whose `endDate` is within the last 180 days. Override `inactiveStatusField`, `inactiveStatusValues`, or `inactiveDateField` if your tenant uses different fields or status codes.

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
