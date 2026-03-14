# SuccessFactors to Active Directory Sync

[![Test](https://github.com/0Downtime/sf-ad-sync/actions/workflows/test.yml/badge.svg?branch=main)](https://github.com/0Downtime/sf-ad-sync/actions/workflows/test.yml)
[![Security](https://github.com/0Downtime/sf-ad-sync/actions/workflows/security.yml/badge.svg?branch=main)](https://github.com/0Downtime/sf-ad-sync/actions/workflows/security.yml)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://github.com/0Downtime/sf-ad-sync/blob/main/LICENSE)

PowerShell automation for syncing SAP SuccessFactors worker data into on-premises Active Directory.

> [!WARNING]
> This project is still in active development and is not ready for production use.

## What It Does
- Pulls workers from SAP SuccessFactors OData v2 using OAuth2 client credentials.
- Maps a stable SuccessFactors employee identifier to AD as the authoritative join key.
- Creates and updates AD users for employees and prehires.
- Enables prehires 7 days before start date.
- Disables terminated users, moves them to a graveyard OU, suppresses further sync, and deletes them after retention.
- Supports additional Core HR mappings such as job title, business unit, division, cost center, and employment class when exposed by the tenant query.
- Supports per-field mapping toggles, dry-run mode, delta sync, and periodic full reconciliation.

## Feature Comparison
Comparison against the Microsoft Entra SuccessFactors connector documented here: [Configure SAP SuccessFactors to Active Directory user provisioning](https://learn.microsoft.com/en-us/entra/identity/saas-apps/sap-successfactors-inbound-provisioning-tutorial).

| Feature | `sf-ad-sync` | Microsoft Entra SuccessFactors connector |
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
| Per-worker replay and on-demand provisioning test | 🗓️ Planned | ✅ |
| Email write-back to SuccessFactors for downstream process needs | 🗓️ Planned | ✅ |
| Hosted admin portal for mapping management, dry-run review, run history, and approvals | 🗓️ Planned | ✅ |

## Planned Features
Planned work is ordered by delivery priority so the roadmap is easy to scan from immediate operational needs to longer-term product improvements. The planned items in the comparison table use the same names as the roadmap below.

### Near Term
- Manual review workflow with operator actions for quarantined workers, unresolved managers, and rehire cases.
- Approval mode for high-risk actions such as disables, deletes, and graveyard OU moves.
- Alerting hooks for failed runs, guardrail breaches, and manual-review events.
- Per-worker replay and on-demand provisioning test mode to inspect mapping, matching, and lifecycle decisions.

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
- `src/Invoke-SfAdSync.ps1`: main sync entrypoint.
- `src/Modules/SfAdSync`: config, state, mapping, reporting, sync orchestration, rollback, SuccessFactors, and AD modules.
- `config`: sample tenant config and mapping config.
- `scripts/Register-SfAdSyncScheduledTask.ps1`: scheduled task bootstrap.
- `scripts/Get-SfAdSyncStatus.ps1`: summary view of the latest sync report and runtime state.
- `scripts/Watch-SfAdSyncMonitor.ps1`: terminal dashboard for current sync stage and recent run history.
- `scripts/Invoke-TestSuite.ps1`: run the Pester test suite.
- `scripts/Undo-SfAdSyncRun.ps1`: rollback one sync run using the recorded operation journal.
- `tests`: Pester tests for config and mapping behavior.

## Setup
1. Copy `config/sample.real-successfactors.real-ad.sync-config.json` and `config/sample.successfactors-to-ad.mapping-config.json` to environment-specific files.
2. Fill in SuccessFactors OAuth details, tenant query fields, OU routing, and licensing groups. Only fill in the AD server and bind credentials if you are running from a non-domain-joined host or need to target a specific DC.
3. Confirm the immutable SuccessFactors identity field and the AD attribute that stores it.
4. Install RSAT Active Directory tools and ensure the host can reach SuccessFactors.
5. Validate any nested SuccessFactors fields you want to sync and align them to your tenant metadata.
6. Run a dry-run first.

## Usage
```powershell
pwsh ./src/Invoke-SfAdSync.ps1 `
  -ConfigPath ./config/sample.real-successfactors.real-ad.sync-config.json `
  -MappingConfigPath ./config/sample.successfactors-to-ad.mapping-config.json `
  -Mode Delta `
  -DryRun
```

If you want the script to prompt for missing runtime values such as OAuth secrets, AD bind credentials, or the default AD password, use:

```powershell
pwsh ./scripts/Invoke-SfAdSyncInteractive.ps1 `
  -ConfigPath ./config/sample.real-successfactors.real-ad.sync-config.json `
  -MappingConfigPath ./config/sample.successfactors-to-ad.mapping-config.json `
  -Mode Delta `
  -DryRun
```

Run a preflight validation before the first sync or after config changes:

```powershell
pwsh ./scripts/Invoke-SfAdPreflight.ps1 `
  -ConfigPath ./config/sample.real-successfactors.real-ad.sync-config.json `
  -MappingConfigPath ./config/sample.successfactors-to-ad.mapping-config.json
```

To view the current sync status from the configured state/report files:

```powershell
pwsh ./scripts/Get-SfAdSyncStatus.ps1 `
  -ConfigPath ./config/sample.real-successfactors.real-ad.sync-config.json
```

Use `-AsJson` if you want the status in machine-readable form.
Use `-IncludeCurrentRun` to print the live runtime snapshot and `-IncludeHistory -HistoryLimit 10` to print recent runs in plain text.

To watch the current stage/progress and the last few syncs in a terminal dashboard:

```powershell
pwsh ./scripts/Watch-SfAdSyncMonitor.ps1 `
  -ConfigPath ./config/sample.real-successfactors.real-ad.sync-config.json
```

Press `q` to quit or `r` to refresh immediately.

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
pwsh ./scripts/Invoke-SyntheticSfAdDryRun.ps1 `
  -UserCount 1000 `
  -OutputDirectory ./reports/synthetic
```

Use `-MaxCreatesPerRun` to intentionally test guardrail failures, `-DuplicateWorkerIdCount` to inject duplicate source identities, and `-ExistingUpnCollisionCount` to simulate create-time UPN collisions.
The harness also assigns each synthetic user a manager from a generated manager directory so the `manager` attribute is populated in the dry-run create payloads.

To run the real sync against a local mock SuccessFactors API instead of a tenant:

1. Start the mock API in one terminal:

```powershell
pwsh ./scripts/Start-MockSuccessFactorsApi.ps1 `
  -UserCount 1000 `
  -ManagerCount 50 `
  -Port 18080
```

2. Point the sync at `./config/sample.mock-successfactors.real-ad.sync-config.json` or a copy of it.

3. Run preflight:

```powershell
pwsh ./scripts/Invoke-SfAdPreflight.ps1 `
  -ConfigPath ./config/sample.mock-successfactors.real-ad.sync-config.json `
  -MappingConfigPath ./config/sample.successfactors-to-ad.mapping-config.json
```

4. Run the actual sync command against the mock API. Use `-DryRun` first, then remove it when you are ready to create lab users:

```powershell
pwsh ./src/Invoke-SfAdSync.ps1 `
  -ConfigPath ./config/sample.mock-successfactors.real-ad.sync-config.json `
  -MappingConfigPath ./config/sample.successfactors-to-ad.mapping-config.json `
  -Mode Full `
  -DryRun
```

The mock server exposes:
- OAuth token URL: `http://127.0.0.1:18080/oauth/token`
- OData base URL: `http://127.0.0.1:18080/odata/v2`
- Metadata endpoint: `http://127.0.0.1:18080/odata/v2/$metadata`

To roll back a specific run from its report file:

```powershell
pwsh ./scripts/Undo-SfAdSyncRun.ps1 `
  -ReportPath ./reports/output/sf-ad-sync-Delta-20260306-090018.json `
  -ConfigPath ./config/sample.real-successfactors.real-ad.sync-config.json `
  -DryRun
```

Remove `-DryRun` to apply the rollback.

## Releases
- `main` publishes a prerelease for runtime-affecting pushes using the current `VERSION` value plus CI metadata, for example `0.1.0-dev.42+sha.a1b2c3d`.
- Documentation-only and workflow-only changes do not publish a prerelease.
- Stable releases are cut manually from GitHub Actions and must match the root `VERSION` file exactly.
- Release bundles include the runtime deployment content: `src`, `scripts`, `config`, `README.md`, `LICENSE`, `SECURITY.md`, and `CONTRIBUTING.md`.

## Notes
- This software is provided as-is and is used at your own risk. You are responsible for validating configuration, testing changes safely, and assessing operational impact before using it in any environment. The maintainers are not responsible for data loss, directory damage, outages, or other issues caused by use or misuse of this project.
- Secret values can be supplied through environment variables referenced by `config.secrets`; those values override plaintext config settings.
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
