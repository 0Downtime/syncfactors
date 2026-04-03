# [Alpha] SyncFactors

`SyncFactors` is the current .NET-based SyncFactors implementation and repository root.

> [!WARNING]
> [Alpha] This software is in active development, has a high risk of failure, and is not ready for production use.
> Expect breaking changes, incomplete workflows, missing features, and operational defects. Validate everything in a non-production environment first.

## Current State

- The active implementation is a local-first .NET 10 solution built around ASP.NET Core, a background worker, and SQLite-backed runtime state.
- The repository already contains operator-facing UI flows, run history, scheduling, dependency health probes, worker preview/apply flows, and local authentication.
- Production readiness is not implied by the current feature set, repository layout, sample config, or helper scripts.

## Goals

- Keep the product local-first and operator-friendly.
- Preserve dry-run, review, approvals, rollback, and auditability as first-class capabilities.
- Replace shell-driven orchestration with typed domain services and explicit job/state models where practical.
- Maintain a path from a single-tenant Windows admin tool to a future hosted control plane if that becomes necessary.

## Current Stack

- Runtime: .NET 10
- Operator UI: ASP.NET Core Razor Pages
- Background execution: .NET hosted worker service
- Runtime state: SQLite
- Directory integration: .NET plus PowerShell seams where needed
- Tests: xUnit across API, domain, infrastructure, and mock SuccessFactors projects

## Solution Shape

- `src/SyncFactors.Api`: local operator UI plus authenticated JSON endpoints for status, dashboard, health, runs, and schedule management
- `src/SyncFactors.Worker`: background host that claims queued runs, executes sync work, records heartbeats, and processes recurring schedules
- `src/SyncFactors.MockSuccessFactors`: local SuccessFactors-like API plus fixture generation tooling for development and testing
- `src/SyncFactors.Domain`: run orchestration, preview/apply behavior, lifecycle rules, scheduling, and sync coordination
- `src/SyncFactors.Infrastructure`: SQLite persistence, Active Directory access, SuccessFactors client logic, local auth, filesystem helpers, and config loading
- `src/SyncFactors.Contracts`: shared runtime DTOs and status models
- `tests/*`: unit and integration test projects aligned to the runtime components above
- `config/*`: tracked sample config, mock fixture data, and scaffold data
- `docs/architecture.md`: architecture direction and system boundaries
- `docs/empjob-ad-mapping.md`: current field mapping notes for the `EmpJob` flow

I am also comparing Razor Pages with the Vite-based UI spike in `frontend-spike/`. The current operator surface is still Razor Pages first.

## What Works Today

- Operator dashboard with current runtime status, recent runs, active run summary, and dependency health
- Ad hoc run queueing for dry-run and live syncs
- Recurring full-sync schedule configuration backed by SQLite
- Run history and run detail pages
- Worker preview flow that stages one worker, persists the preview, and supports explicit apply from the saved fingerprint
- Local username/password authentication with cookie auth and an admin-only user management page
- Mock SuccessFactors API for local development, fixture playback, and synthetic worker population
- Delete-all testing reset flow from the Sync page

> [!CAUTION]
> The delete-all/testing reset flow is destructive. It exists for controlled testing and operator workflows and should be treated as dangerous even in non-production environments.

## Status

The solution builds from the repository root against the locally installed .NET 10 SDK.

The repository is still alpha. The implementation is concrete enough to document current operator flows, but APIs, config shape, storage details, and operational behavior may still change materially.

## Local Development

Primary commands from the repository root:

```powershell
dotnet build ./SyncFactors.Next.sln
dotnet test ./SyncFactors.Next.sln
```

The helper scripts under `scripts/` and `scripts/codex/` are the current supported launch path for the local stack.

## Config Model

The rewrite keeps tracked samples and ignored local config under `config/`.

- `config/sample.mock-successfactors.real-ad.sync-config.json`: sample config for mock SuccessFactors plus real Active Directory
- `config/sample.real-successfactors.real-ad.sync-config.json`: sample config for real SuccessFactors plus real Active Directory
- `config/sample.empjob-confirmed.mapping-config.json`: sample mapping config for the current `EmpJob`-driven flow
- `config/local*.json`: local editable copies created by the worktree bootstrap script when missing

Sync config resolution currently works like this:

1. `.env.worktree` sets `SYNCFACTORS_RUN_PROFILE` to `mock` or `real`
2. If `SYNCFACTORS_CONFIG_PATH` is set, that explicit path wins
3. Otherwise the active profile resolves to `config/local.mock-successfactors.real-ad.sync-config.json` or `config/local.real-successfactors.real-ad.sync-config.json`
4. Mapping config resolves from `SYNCFACTORS_MAPPING_CONFIG_PATH`, or defaults to `config/local.syncfactors.mapping-config.json`

`.env.worktree` is the main per-worktree environment contract. Keep auth, profile selection, ports, and local overrides there rather than in tracked JSON or tracked `.codex` files.

On Windows, `scripts/codex/Load-WorktreeEnv.ps1` checks the worktree-scoped Windows Credential Manager entry for each variable first, then falls back to `.env.worktree`, then `.env.worktree.example`, and finally built-in defaults where applicable.

The checked-in example currently includes:

```bash
SYNCFACTORS_RUN_PROFILE=mock
SYNCFACTORS_CONFIG_PATH=
SYNCFACTORS_MAPPING_CONFIG_PATH=./config/local.syncfactors.mapping-config.json
SYNCFACTORS_SQLITE_PATH=state/runtime/syncfactors.db
SYNCFACTORS_API_PORT=5087
MOCK_SF_PORT=18080
MOCK_SF_SYNTHETIC_POPULATION_ENABLED=true
MOCK_SF_TARGET_WORKER_COUNT=1000
SYNCFACTORS_KEYCHAIN_SERVICE=syncfactors
SF_AD_SYNC_SF_USERNAME=
SF_AD_SYNC_SF_PASSWORD=
SF_AD_SYNC_SF_CLIENT_ID=mock-client-id
SF_AD_SYNC_SF_CLIENT_SECRET=mock-client-secret
SF_AD_SYNC_AD_SERVER=
SF_AD_SYNC_AD_USERNAME=
SF_AD_SYNC_AD_BIND_PASSWORD=
SF_AD_SYNC_AD_DEFAULT_PASSWORD=
```

On macOS, you can keep sensitive `SF_AD_SYNC_*` values out of `.env.worktree` entirely and store them in the login Keychain instead. The launchers fall back to the Keychain service named by `SYNCFACTORS_KEYCHAIN_SERVICE` when those variables are blank in `.env.worktree`. To store one:

```bash
./scripts/codex/set-macos-keychain-secret.sh SF_AD_SYNC_AD_BIND_PASSWORD
```

To import the full worktree secret set in one pass:

```bash
./scripts/codex/save-worktree-env-to-macos-keychain.sh
```

To enter selected values interactively and verify each save succeeded:

```bash
./scripts/codex/save-worktree-env-to-macos-keychain.sh --interactive SF_AD_SYNC_AD_BIND_PASSWORD SF_AD_SYNC_AD_DEFAULT_PASSWORD
```

On Windows, you can import worktree values into Windows Credential Manager with:

```powershell
pwsh ./scripts/codex/Save-WorktreeEnvToWindowsCredentialManager.ps1
```

Before starting the API on a new admin workstation, run:

```powershell
pwsh ./scripts/Install-SyncFactorsHttpsCertificate.ps1
```

That script generates, trusts, and exports the local HTTPS certificate used by the API launcher. `scripts/Start-SyncFactorsNextApi.ps1` and `scripts/codex/run.ps1 -Service api` now bind `https://127.0.0.1:<port>` only and refuse `http://` URLs. If `SYNCFACTORS_TLS_CERT_PATH` and `SYNCFACTORS_TLS_CERT_PASSWORD` are not set explicitly, the launcher uses the exported certificate from that install step.

If you already have a CA-issued `.pfx`, use:

```powershell
pwsh ./scripts/Install-SyncFactorsHttpsCertificateFromPfx.ps1 -PfxPath C:\path\to\syncfactors-api.pfx -PfxPassword '<password>'
```

That script copies your PFX into the same runtime certificate location used by the launcher, writes the matching password file, and on Windows imports the certificate into the `My` store. Add `-StoreLocation LocalMachine` to target the machine store instead of the current user store, or `-SkipStoreImport` if you only want to configure the app runtime files.

Use `--remove-empty-values` on macOS or `-RemoveEmptyValues` on Windows if blank entries in `.env.worktree` should delete the corresponding stored credentials instead of saving empty strings.

Set `SYNCFACTORS_RUN_PROFILE=mock` or `real` to switch the active SuccessFactors config. Leave `SYNCFACTORS_CONFIG_PATH` empty for profile-based resolution, or set it only when you want an explicit one-off override.

For Active Directory binds, the current .NET LDAP integration uses simple bind semantics. Set `SF_AD_SYNC_AD_USERNAME` to a UPN such as `svc_successfactors@example.local`, not a down-level logon name such as `EXAMPLE\svc_successfactors`, or AD may reject the credentials even when the password is correct.

For full-sync `EmpJob` queries, `successFactors.query.inactiveRetentionDays` can extend the source filter to keep recently inactive workers in scope without hand-writing the date cutoff in `baseFilter`. With the default fields, a config like `"baseFilter": "emplStatus in 'A','U'"` plus `"inactiveRetentionDays": 180` expands to include terminated (`emplStatus eq 'T'`) workers whose `endDate` is within the last 180 days. Override `inactiveStatusField`, `inactiveStatusValues`, or `inactiveDateField` if your tenant uses different fields or status codes.

## Local Auth

The API uses local username/password authentication backed by SQLite.

- Cookie auth protects the operator UI and authenticated API routes
- The admin user management page lives under `/Admin/Users`
- Admin accounts can create users, reset passwords, change roles, deactivate accounts, and delete users

On first startup, if no local users exist, the API requires bootstrap admin credentials to be configured through `SyncFactors:Auth:BootstrapAdmin:Username` and `SyncFactors:Auth:BootstrapAdmin:Password`. If those values are missing and the user store is empty, startup fails intentionally.

## Running The Local Stack

Bootstrap a checkout or worktree first:

```powershell
pwsh ./scripts/codex/setup-worktree.ps1
```

That script:

- creates runtime/report directories when missing
- creates `config/local.mock-successfactors.real-ad.sync-config.json` when missing
- creates `config/local.real-successfactors.real-ad.sync-config.json` when missing
- creates `config/local.syncfactors.mapping-config.json` when missing
- creates `.env.worktree` from `.env.worktree.example` when missing
- copies ignored local config from the primary worktree first when available

The intended local loop is:

```powershell
pwsh ./scripts/codex/setup-worktree.ps1
pwsh ./scripts/codex/run.ps1 -Service mock
pwsh ./scripts/codex/run.ps1 -Service api
pwsh ./scripts/codex/run.ps1 -Service worker
```

If you are using Windows Credential Manager, import values before launching services:

```powershell
pwsh ./scripts/codex/Save-WorktreeEnvToWindowsCredentialManager.ps1
```

Or start the profile-aware stack in one command:

```powershell
pwsh ./scripts/codex/run.ps1 -Service stack
```

Useful variants:

- `pwsh ./scripts/codex/run.ps1 -Service stack -Profile mock`
- `pwsh ./scripts/codex/run.ps1 -Service stack -Profile real`
- `pwsh ./scripts/codex/run.ps1 -Service stack -Restart`
- `pwsh ./scripts/codex/run.ps1 -Service api -SkipBuild`

When you run `-Service stack`, the launched services depend on the active profile:

- `mock`: starts the mock SuccessFactors API, the SyncFactors API, and the worker
- `real`: starts the SyncFactors API and the worker

The lower-level start scripts remain available if you need to launch individual components directly:

- `scripts/Start-SyncFactorsMockSuccessFactors.ps1`
- `scripts/Start-SyncFactorsNextApi.ps1`
- `scripts/Start-SyncFactorsWorker.ps1`

## Codex Worktrees On macOS

Codex app worktrees can bootstrap this repository through the checked-in local environment at `.codex/environments/environment.toml`. Open the project in the Codex app, choose the local environment when starting a worktree thread, and Codex will run `scripts/codex/setup-worktree-macos.sh` on worktree creation. That macOS wrapper delegates to the shared PowerShell bootstrap script so the setup behavior matches Windows.

This setup is intentionally scoped to the core local dev loop:

- prepare local config files for the .NET rewrite when missing
- copy ignored local runtime files from the primary checkout when missing
- fall back to tracked `config/sample*.json` files when local config files are still missing
- create runtime/report directories used by the API and worker
- fall back to `.env.worktree.example` when `.env.worktree` is still missing

For project-scoped `.codex` settings to load, this repo or one of its parent paths must be marked trusted in `~/.codex/config.toml`. Codex skips project-scoped `.codex` layers for untrusted projects.

## Mock SuccessFactors

Use `src/SyncFactors.MockSuccessFactors` to run a local SuccessFactors-like API for development.

- Preferred launcher: `scripts/Start-SyncFactorsMockSuccessFactors.ps1`
- Direct run: `dotnet run --project src/SyncFactors.MockSuccessFactors --no-launch-profile`
- Default URL: `http://127.0.0.1:18080`
- Baseline fixture data: `config/mock-successfactors/baseline-fixtures.json`
- Sample import data for sanitization: `config/mock-successfactors/sample-export.json`

Generate sanitized fixtures from exported OData payloads with:

```bash
dotnet run --project src/SyncFactors.MockSuccessFactors -- \
  generate-fixtures \
  --input config/mock-successfactors/sample-export.json \
  --output /tmp/sanitized-fixtures.json \
  --manifest /tmp/sanitized-fixtures.manifest.json
```

The mock intentionally supports the query shapes used by the current SyncFactors client: OAuth or Basic auth, `PerPerson` for preview, `EmpJob` for the main worker query, `$format=json`, `$filter` on `personIdExternal` or `userId`, and the current `$select` and `$expand` paths used by the client.

If you need to capture a real `PerPerson` payload before sanitizing it, use:

- `scripts/Export-SfPerPerson.ps1` for OAuth client-credentials auth
- `scripts/Export-SfPerPerson-Basic.ps1` for Basic auth

If you need an admin-safe handoff file, use `scripts/Export-SfPerPerson-Sanitized.ps1`. It fetches the response, sanitizes it in memory, and writes only the sanitized JSON to disk. Add `-AliasOrgValues` if company, department, or location labels should also be anonymized, and `-KeepPersonIdExternal` if you need to preserve the source worker ID.

If your tenant rejects one of the optional fields in the hard-coded query, all three export scripts support `-ExcludeSelectPath` and `-ExcludeExpandPath`. For example, to skip business unit:

```powershell
-ExcludeSelectPath "employmentNav/jobInfoNav/businessUnitNav/businessUnit" `
-ExcludeExpandPath "employmentNav/jobInfoNav/businessUnitNav"
```

If you want to probe the broader employee header set for one worker without sanitizing the output, use `scripts/Export-SfEmployeeHeaderProfile.ps1`. It wraps the same auto-retrying query logic with `-IncludeHeaderProfile -SkipSanitization` already enabled so you can see which fields your tenant actually exposes.

If you want to discover likely field mappings from tenant metadata before querying a worker, use `scripts/Get-SfMetadataFieldCandidates.ps1`. It downloads `/$metadata`, searches for the employee headers we discussed, and writes candidate entities and OData paths to JSON.

If you want to query one worker using the tighter, metadata-derived employee field set, use `scripts/Export-SfEmployeeMetadataProfile.ps1`. It targets the strongest candidate paths from the metadata analysis, skips sanitization, and keeps the same auto-retry behavior for unsupported tenant fields.
