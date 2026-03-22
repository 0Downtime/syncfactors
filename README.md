# [Alpha] SyncFactors

[![Test](https://github.com/0Downtime/syncfactors/actions/workflows/test.yml/badge.svg?branch=main)](https://github.com/0Downtime/syncfactors/actions/workflows/test.yml)
[![Security](https://github.com/0Downtime/syncfactors/actions/workflows/security.yml/badge.svg?branch=main)](https://github.com/0Downtime/syncfactors/actions/workflows/security.yml)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://github.com/0Downtime/syncfactors/blob/main/LICENSE)

SyncFactors is PowerShell automation for syncing SAP SuccessFactors worker data into on-premises Active Directory.

> [!WARNING]
> Alpha status only. This application has a high chance of being broken, is still in active development, and is not ready for production use.
> No stable builds are available yet.

## What It Does
- Pulls workers from SAP SuccessFactors OData v2 using configurable basic auth or OAuth2 client credentials.
- Maps a stable SuccessFactors employee identifier to AD as the authoritative join key.
- Creates and updates AD users for employees and prehires.
- Enables prehires 7 days before start date.
- Disables terminated users, moves them to a graveyard OU, suppresses further sync, and deletes them after retention.
- Supports approval mode for broad-run disables, deletes, and graveyard OU moves, with per-worker operator apply after review.
- Supports additional Core HR mappings such as job title, business unit, division, cost center, and employment class when exposed by the tenant query.
- Supports per-field mapping toggles, dry-run mode, delta sync, and periodic full reconciliation.

## Feature Comparison
Comparison against the Microsoft Entra SuccessFactors connector documented here: [Configure SAP SuccessFactors to Active Directory user provisioning](https://learn.microsoft.com/en-us/entra/identity/saas-apps/sap-successfactors-inbound-provisioning-tutorial).

| Feature | `syncfactors` | Microsoft Entra SuccessFactors connector |
| --- | --- | --- |
| SuccessFactors Employee Central as the authoritative HR source for AD provisioning | ✅ | ✅ |
| AD account create, update, disable, and lifecycle sync | ✅ | ✅ |
| Attribute mapping and stable matching ID support | ✅ | ✅ |
| Customizable mapping, routing, and lifecycle behavior | ✅ | ⚠️ Limited |
| Prehire and rehire handling | ✅ | ⚠️ Limited |
| Scoped rollout controls for testing limited populations | ✅ | ⚠️ Limited |
| Easy-to-read logs and explicit change reports for each run | ✅ | ❌ |
| Rollback of recorded AD changes | ✅ | ❌ |
| Delta sync plus periodic full reconciliation | ✅ | ✅ |
| Audit logs and sync run reporting | ✅ | ✅ |
| Per-worker replay and on-demand provisioning test | ✅ | ✅ |
| Email write-back to SuccessFactors for downstream process needs | 🗓️ Planned | ✅ |
| Hosted admin portal for mapping management, dry-run review, run history, and approvals | 🗓️ Planned | ✅ |

## Planned Features
Planned work is ordered by delivery priority so the roadmap is easy to scan from immediate operational needs to longer-term product improvements. The planned items in the comparison table use the same names as the roadmap below.

### Near Term
- Alerting hooks for failed runs, guardrail breaches, and manual-review events.

### Mid Term
- Expanded policy engine for prehire, rehire, leave-of-absence, contractor, transfer, and termination handling.
- Operator workflows for resolving duplicate email, UPN, employee ID, and ambiguous identity matches.
- Conditional group provisioning rules based on company, department, location, or worker type.
- Manager resolution retry and fallback strategies beyond the current quarantine behavior.
- SuccessFactors schema discovery to help build mapping configs from tenant metadata.
- Attribute-level protection rules such as preserving operator-managed fields.

### Longer Term
- Hosted admin portal for mapping management, dry-run review, run history, and approvals.
- Email write-back to SuccessFactors where downstream process requirements need it.
- Effective-dated change preview for future hires, transfers, and terminations.
- Drift detection for AD state, mapping behavior, and config changes across runs.
- Plugin-based custom transforms and matching extensions without forking the project.
- Versioned config migration support as the config schema evolves.
- Expanded regression fixture packs for replaying tenant-specific edge cases in tests.

## Project Layout
- `src/Invoke-SyncFactors.ps1`: main sync entrypoint.
- `src/Modules/SyncFactors`: config, state, mapping, reporting, sync orchestration, rollback, SuccessFactors, and AD modules.
- `config`: sample tenant config and mapping config.
- `scripts/Register-SyncFactorsScheduledTask.ps1`: scheduled task bootstrap.
- `scripts/Get-SyncFactorsStatus.ps1`: summary view of the latest sync state, runtime status, and run history from SQLite.
- `scripts/Watch-SyncFactorsMonitor.ps1`: terminal dashboard for current sync stage and recent run history.
- `scripts/Get-SyncFactorsWebStatus.ps1`: PowerShell adapter for the local web dashboard status API.
- `scripts/Invoke-SyncFactorsWorkerPreview.ps1`: preview one worker and print the mapped diff against the current AD user.
- `scripts/Install-SyncFactorsTerminalCommand.ps1`: installs the `syncfactors` terminal command for launching the dashboard from any shell.
- `scripts/Invoke-TestSuite.ps1`: run the Pester test suite.
- `scripts/Undo-SyncFactorsRun.ps1`: rollback one sync run using the recorded operation journal.
- `web`: local read-only web dashboard frontend and API.
- `tests`: Pester tests for config and mapping behavior.

## Setup
1. Start from `config/local.real-successfactors.real-ad.sync-config.json` and `config/local.syncfactors.mapping-config.json` for your real environment. Those `local.*` files are ignored by git so you can store tenant-specific AD and SuccessFactors settings safely outside version control.
2. Fill in the SuccessFactors auth block, tenant query fields, OU routing, and licensing groups. The real sample config defaults to basic auth, while the mock sample uses OAuth. Only fill in the AD server and bind credentials if you are running from a non-domain-joined host or need to target a specific DC.
   If your SuccessFactors OAuth token endpoint requires HTTP Basic client authentication, set `successFactors.auth.oauth.clientAuthentication` to `basic`; leave it as `body` when the endpoint expects `client_id` and `client_secret` in the form body.
3. Confirm the immutable SuccessFactors identity field and the AD attribute that stores it.
4. Install RSAT Active Directory tools and ensure the host can reach SuccessFactors.
5. Validate any nested SuccessFactors fields you want to sync and align them to your tenant metadata.
6. Run a dry-run first.

## Usage
If you want the terminal dashboard to be the main operator entry point, install the `syncfactors` command once:

```powershell
pwsh ./scripts/Install-SyncFactorsTerminalCommand.ps1 `
  -ConfigPath ./config/local.real-successfactors.real-ad.sync-config.json `
  -MappingConfigPath ./config/local.syncfactors.mapping-config.json
```

The installer writes `syncfactors`, `syncfactors.cmd`, and `syncfactors.ps1` shims into a user bin directory, updates `PATH` when needed, and points them at your chosen config files. Open a new terminal after the install if your shell has not picked up the PATH change yet, then launch the dashboard with:

```powershell
syncfactors
```

The command supports the dashboard's normal monitor flags from [`scripts/Watch-SyncFactorsMonitor.ps1`](/Users/chrisbrien/dev/github.com/syncfactors/scripts/Watch-SyncFactorsMonitor.ps1), for example `syncfactors -RunOnce -AsText`.

To repoint the installed TUI at a different config later, use the companion helper command:

```powershell
syncfactors-config -ConfigPath ./config/other.sync-config.json `
  -MappingConfigPath ./config/other.mapping-config.json
```

To inspect the current default config paths:

```powershell
syncfactors-config -ShowCurrent
```

To uninstall the command shims later:

```powershell
pwsh ./scripts/Install-SyncFactorsTerminalCommand.ps1 -Uninstall
```

If you also want the installer to remove the PATH entry it added, use:

```powershell
pwsh ./scripts/Install-SyncFactorsTerminalCommand.ps1 -Uninstall -RemovePathUpdate
```

```powershell
pwsh ./src/Invoke-SyncFactors.ps1 `
  -ConfigPath ./config/local.real-successfactors.real-ad.sync-config.json `
  -MappingConfigPath ./config/local.syncfactors.mapping-config.json `
  -Mode Delta `
  -DryRun
```

If you want the script to prompt for missing runtime values such as SuccessFactors credentials, AD bind credentials, or the default AD password, use:

```powershell
pwsh ./scripts/Invoke-SyncFactorsInteractive.ps1 `
  -ConfigPath ./config/local.real-successfactors.real-ad.sync-config.json `
  -MappingConfigPath ./config/local.syncfactors.mapping-config.json `
  -Mode Delta `
  -DryRun
```

Run a preflight validation before the first sync or after config changes:

```powershell
pwsh ./scripts/Invoke-SyncFactorsPreflight.ps1 `
  -ConfigPath ./config/local.real-successfactors.real-ad.sync-config.json `
  -MappingConfigPath ./config/local.syncfactors.mapping-config.json
```

To view the current sync status from the configured SQLite operational store:

```powershell
pwsh ./scripts/Get-SyncFactorsStatus.ps1 `
  -ConfigPath ./config/local.real-successfactors.real-ad.sync-config.json
```

Use `-AsJson` if you want the status in machine-readable form.
Use `-IncludeCurrentRun` to print the live runtime snapshot and `-IncludeHistory -HistoryLimit 10` to print recent runs in plain text.

To preview a single worker by the configured SuccessFactors identity field and see the proposed AD diff without mutating AD or sync state:

```powershell
pwsh ./scripts/Invoke-SyncFactorsWorkerPreview.ps1 `
  -ConfigPath ./config/local.real-successfactors.real-ad.sync-config.json `
  -MappingConfigPath ./config/local.syncfactors.mapping-config.json `
  -WorkerId 1000123 `
  -PreviewMode Minimal
```

The `-WorkerId` value must match `successFactors.query.identityField`. In the sample configs that field is `personIdExternal`.
Use `-PreviewMode Minimal` to use `successFactors.previewQuery`, `-PreviewMode Full` to force the main `successFactors.query`, or omit it to preserve the configured default behavior.
Use `-AsJson` for machine-readable output. Preview and review runs are persisted in SQLite and returned as run references such as `run:<id>`.
If your tenant metadata is still being validated, set `successFactors.previewQuery` to the smallest confirmed-valid field list, starting with just `personIdExternal`. Single-worker preview uses `previewQuery` when present, while full and delta sync continue using `successFactors.query`.

To require operator approval before broad sync runs can disable accounts, delete accounts, or move users into the graveyard OU, enable approval mode in the sync config:

```json
"approval": {
  "enabled": true,
  "requireFor": ["DisableUser", "DeleteUser", "MoveToGraveyardOu"]
}
```

When approval mode is enabled, delta/full runs convert those high-risk changes into manual-review cases instead of applying them immediately. After review, use the scoped worker preview and one-worker apply flow to execute the approved change intentionally.

To delete all AD user objects found recursively under the managed sync OUs and reset local sync state for a true fresh sync:

```powershell
pwsh ./scripts/Invoke-SyncFactorsFreshSyncReset.ps1 `
  -ConfigPath ./config/local.real-successfactors.real-ad.sync-config.json
```

This targets the configured sync user OUs only: `ad.defaultActiveOu`, `ad.graveyardOu`, and any `ad.ouRoutingRules[].targetOu`.
The reset requires three separate typed confirmations before any deletion happens.

To watch the current stage/progress and the last few syncs in a terminal dashboard:

```powershell
pwsh ./scripts/Watch-SyncFactorsMonitor.ps1 `
  -ConfigPath ./config/local.real-successfactors.real-ad.sync-config.json
```

Press `q` to quit, `r` to refresh immediately, or `t` to pause/resume auto-refresh. Start paused with `-PauseAutoRefresh` if you want to browse the dashboard without timed redraws. The dashboard shortcuts now cover the full run set: `d` delta dry-run, `s` delta sync, `f` full dry-run, `a` full sync, `v` review, `w` single-worker preview, and `z` fresh reset. The `w` shortcut now asks whether to run a `minimal` or `full` worker preview before it launches. The `z` shortcut launches the fresh sync reset script, which still requires three typed confirmations before any deletion happens. Any shortcut that can write anything, including reports, temp exports, or the clipboard, now requires typing `YES` before it proceeds.

To launch the new localhost-only read-only web dashboard:

```bash
npm install --cache /tmp/syncfactors-npm-cache
npm run web:dev -- --config ./config/local.real-successfactors.real-ad.sync-config.json
```

The dev server binds to `127.0.0.1:4280` by default and reads the same SQLite operational store that the TUI uses. To build the frontend bundle for a local production-style run:

```bash
npm run web:build
npm run web:start -- --config ./config/local.real-successfactors.real-ad.sync-config.json
```

### SQLite Operational Store
SQLite is now the operational source of truth for the app.
By default the database path is derived from `state.path` as `syncfactors.db` in the same directory, or you can set `persistence.sqlitePath` explicitly in your sync config.

SQLite stores the live operational model for:
- tracked-worker state
- runtime status snapshots
- run summaries
- run entry rows used by queue, worker, and run-detail views
- stored report payloads used by review, preview, rollback, and worker drill-down flows

Operational writes now go to SQLite:
- sync state
- runtime status
- completed and in-progress run/report data

Run-producing scripts now return SQLite run references such as `run:<id>` rather than requiring a report JSON file path.

JSON is no longer used as an operational fallback by the TUI, status commands, or web dashboard.
The remaining JSON output is fixture/export-oriented:
- demo data generation writes sample artifact files for local browsing and test fixtures
- fresh reset still writes an explicit preview artifact
- the SQLite import script exists for migrating legacy JSON datasets

To backfill SQLite from an existing config that already has JSON state, runtime status, and reports:

```powershell
pwsh ./scripts/Import-SyncFactorsSqlite.ps1 `
  -ConfigPath ./config/local.real-successfactors.real-ad.sync-config.json
```

Use `-AsJson` for machine-readable output, or `-SkipState`, `-SkipRuntimeStatus`, and `-SkipReports` to backfill only part of the dataset.
Once the SQLite file exists, the web dashboard, status commands, and terminal monitor use it as the authoritative store. If the database is missing, those operational views now fail instead of silently falling back to JSON.

To run the full Pester suite:

```powershell
pwsh ./scripts/Invoke-TestSuite.ps1 -Detailed
```

To print a coverage summary and compare it to the current non-blocking baseline:

```powershell
pwsh ./scripts/Invoke-TestSuite.ps1 -Coverage
```

If you want a machine-readable coverage summary for CI or local tooling:

```powershell
pwsh ./scripts/Invoke-TestSuite.ps1 -Coverage -CoverageSummaryPath ./artifacts/test-coverage-summary.json
```

To simulate a large local dry-run without calling SuccessFactors or Active Directory:

```powershell
pwsh ./scripts/Invoke-SyntheticSyncFactorsDryRun.ps1 `
  -UserCount 1000 `
  -OutputDirectory ./reports/synthetic
```

Use `-MaxCreatesPerRun` to intentionally test guardrail failures, `-DuplicateWorkerIdCount` to inject duplicate source identities, and `-ExistingUpnCollisionCount` to simulate create-time UPN collisions.
The harness also assigns each synthetic user a manager from a generated manager directory so the `manager` attribute is populated in the dry-run create payloads.

To generate a richer local demo dataset for the TUI and web UI on macOS without Active Directory or a live SuccessFactors tenant:

```powershell
pwsh ./scripts/Invoke-SyncFactorsDemoData.ps1 `
  -OutputDirectory ./reports/demo
```

The demo generator writes a derived config under `./reports/demo/config/demo.mock-sync-config.json`, seeds the SQLite operational store with tracked-worker state, runtime status, and multiple completed runs, and also emits sample JSON report artifacts across the normal report and review directories for local browsing and fixture coverage.
Use `-Force` to replace an existing demo output tree, `-IncludeActiveRun:$false` if you want the dashboards to start idle, and `-RunCount` if you want more than the default mixed-history set.

Then launch the terminal dashboard against the generated demo config:

```powershell
pwsh ./scripts/Watch-SyncFactorsMonitor.ps1 `
  -ConfigPath ./reports/demo/config/demo.mock-sync-config.json `
  -PauseAutoRefresh
```

Or launch the local web dashboard against the same demo config:

```bash
npm install --cache /tmp/syncfactors-npm-cache
npm run web:dev -- --config ./reports/demo/config/demo.mock-sync-config.json
```

Both dashboards will read the generated run history, review queues, worker history, tracked-worker state, and runtime snapshot from the demo SQLite store.
The demo database is created automatically under `./reports/demo/state/syncfactors.db`.

To run the real sync against a local mock SuccessFactors API instead of a tenant:

1. Start the mock API in one terminal:

```powershell
pwsh ./scripts/Start-MockSuccessFactorsApi.ps1 `
  -UserCount 1000 `
  -ManagerCount 50 `
  -Port 18080
```

2. Point the sync at `./config/local.mock-successfactors.real-ad.sync-config.json`. That file is ignored by git, so you can safely adjust the real AD settings locally.

3. Run preflight:

```powershell
pwsh ./scripts/Invoke-SyncFactorsPreflight.ps1 `
  -ConfigPath ./config/local.mock-successfactors.real-ad.sync-config.json `
  -MappingConfigPath ./config/local.syncfactors.mapping-config.json
```

4. Run the actual sync command against the mock API. Use `-DryRun` first, then remove it when you are ready to create lab users:

```powershell
pwsh ./src/Invoke-SyncFactors.ps1 `
  -ConfigPath ./config/local.mock-successfactors.real-ad.sync-config.json `
  -MappingConfigPath ./config/local.syncfactors.mapping-config.json `
  -Mode Full `
  -DryRun
```

The mock server exposes:
- OAuth token URL: `http://127.0.0.1:18080/oauth/token`
- OData base URL: `http://127.0.0.1:18080/odata/v2`
- Metadata endpoint: `http://127.0.0.1:18080/odata/v2/$metadata`

To roll back a specific run from a report JSON file:

```powershell
pwsh ./scripts/Undo-SyncFactorsRun.ps1 `
  -ReportPath ./reports/output/syncfactors-Delta-20260306-090018.json `
  -ConfigPath ./config/local.real-successfactors.real-ad.sync-config.json `
  -DryRun
```

Remove `-DryRun` to apply the rollback.
Rollback currently still expects a report JSON artifact, not a SQLite run reference.

## Releases
- `main` publishes a prerelease for runtime-affecting pushes using the current `VERSION` value plus CI metadata, for example `0.1.0-dev.42+sha.a1b2c3d`.
- Documentation-only and workflow-only changes do not publish a prerelease.
- Stable releases are cut manually from GitHub Actions and must match the root `VERSION` file exactly.
- Release bundles include the runtime deployment content: `src`, `scripts`, `config`, `README.md`, `LICENSE`, `SECURITY.md`, and `CONTRIBUTING.md`.

## SonarCloud
- Import the repository into SonarCloud before running the workflow.
- In GitHub, set `Settings -> Secrets and variables -> Actions` with secret `SONAR_TOKEN` and variables `SONAR_ORGANIZATION` plus `SONAR_PROJECT_KEY`.
- The repository scan workflow is [`.github/workflows/sonarcloud.yml`](/Users/chrisbrien/dev/github.com/syncfactors/.github/workflows/sonarcloud.yml) and reads stable project settings from [`sonar-project.properties`](/Users/chrisbrien/dev/github.com/syncfactors/sonar-project.properties).

## Notes
- This software is provided as-is and is used at your own risk. You are responsible for validating configuration, testing changes safely, and assessing operational impact before using it in any environment. The maintainers are not responsible for data loss, directory damage, outages, or other issues caused by use or misuse of this project.
- Secret values can be supplied through environment variables referenced by `config.secrets`; those values override plaintext config settings.
- The tracked `config/sample.*.json` files are reference templates. Put real tenant values in the ignored `config/local.*.json` files instead.
- On a domain-joined host, leave `ad.server`, `ad.username`, and `ad.bindPassword` empty and the script will use the machine and user domain context.
- For non-domain-joined hosts, set `ad.server`, `ad.username`, and `ad.bindPassword` or their env-backed secret equivalents so AD cmdlets run against a specific DC with explicit credentials.
- The sample config includes per-run safety thresholds for creates, disables, and deletions. Exceeding a threshold fails the run before the next mutation is applied.
- The sample SuccessFactors entity and field names are placeholders and must be aligned to your tenant metadata before production use.
- Mapping source paths support indexed navigation syntax such as `employmentNav[0].jobInfoNav[0].jobTitle` for effective-dated or collection-backed OData expansions.
- The sample mapping config includes disabled Core HR examples for `title`, `division`, `employeeType`, and extension attributes. Enable them only after confirming your tenant payload and target AD attributes.
- Each sync report now includes an ordered operation journal with before/after rollback data for AD mutations and sync state updates.
- Delete rollback is best effort: the project captures broad AD user state and group memberships, but it cannot restore the original password or every AD-native system property.
- OData v2 is the primary API path. Add `CompoundEmployee` only if tenant field coverage requires it.
- Current SAP references used for this implementation:
  - OData reference guide: `SF_HCM_OData_API_DEV.pdf`
  - SAP API reference guide for OData v2
  - CompoundEmployee guide for fallback coverage assessment
