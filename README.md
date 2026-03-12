# SuccessFactors to Active Directory Sync

PowerShell automation for syncing SAP SuccessFactors worker data into on-premises Active Directory.

## What It Does
- Pulls workers from SAP SuccessFactors OData v2 using OAuth2 client credentials.
- Maps a stable SuccessFactors employee identifier to AD as the authoritative join key.
- Creates and updates AD users for employees and prehires.
- Enables prehires 7 days before start date.
- Disables terminated users, moves them to a graveyard OU, suppresses further sync, and deletes them after retention.
- Supports additional Core HR mappings such as job title, business unit, division, cost center, and employment class when exposed by the tenant query.
- Supports per-field mapping toggles, dry-run mode, delta sync, and periodic full reconciliation.

## Project Layout
- `src/Invoke-SfAdSync.ps1`: main sync entrypoint.
- `src/Modules/SfAdSync`: config, state, mapping, reporting, sync orchestration, rollback, SuccessFactors, and AD modules.
- `config`: sample tenant config and mapping config.
- `scripts/Register-SfAdSyncScheduledTask.ps1`: scheduled task bootstrap.
- `scripts/Get-SfAdSyncStatus.ps1`: summary view of the latest sync report and runtime state.
- `scripts/Invoke-TestSuite.ps1`: run the Pester test suite.
- `scripts/Undo-SfAdSyncRun.ps1`: rollback one sync run using the recorded operation journal.
- `tests`: Pester tests for config and mapping behavior.

## Setup
1. Copy `config/sample.sync-config.json` and `config/sample.mapping-config.json` to environment-specific files.
2. Fill in SuccessFactors OAuth details, tenant query fields, OU routing, and licensing groups.
3. Confirm the immutable SuccessFactors identity field and the AD attribute that stores it.
4. Install RSAT Active Directory tools and ensure the host can reach SuccessFactors.
5. Validate any nested SuccessFactors fields you want to sync and align them to your tenant metadata.
6. Run a dry-run first.

## Usage
```powershell
pwsh ./src/Invoke-SfAdSync.ps1 `
  -ConfigPath ./config/sample.sync-config.json `
  -MappingConfigPath ./config/sample.mapping-config.json `
  -Mode Delta `
  -DryRun
```

Run a preflight validation before the first sync or after config changes:

```powershell
pwsh ./scripts/Invoke-SfAdPreflight.ps1 `
  -ConfigPath ./config/sample.sync-config.json `
  -MappingConfigPath ./config/sample.mapping-config.json
```

To view the current sync status from the configured state/report files:

```powershell
pwsh ./scripts/Get-SfAdSyncStatus.ps1 `
  -ConfigPath ./config/sample.sync-config.json
```

Use `-AsJson` if you want the status in machine-readable form.

To run the full Pester suite:

```powershell
pwsh ./scripts/Invoke-TestSuite.ps1 -Detailed
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

2. Point the sync at [sample.mock-successfactors.sync-config.json](/Users/chrisbrien/dev/github.com/sf-ad-sync/config/sample.mock-successfactors.sync-config.json) or a copy of it.

3. Run preflight:

```powershell
pwsh ./scripts/Invoke-SfAdPreflight.ps1 `
  -ConfigPath ./config/sample.mock-successfactors.sync-config.json `
  -MappingConfigPath ./config/sample.mapping-config.json
```

4. Run the actual sync command against the mock API. Use `-DryRun` first, then remove it when you are ready to create lab users:

```powershell
pwsh ./src/Invoke-SfAdSync.ps1 `
  -ConfigPath ./config/sample.mock-successfactors.sync-config.json `
  -MappingConfigPath ./config/sample.mapping-config.json `
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
  -ConfigPath ./config/sample.sync-config.json `
  -DryRun
```

Remove `-DryRun` to apply the rollback.

## Notes
- Secret values can be supplied through environment variables referenced by `config.secrets`; those values override plaintext config settings.
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
