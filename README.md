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
- `src/Modules/SfAdSync`: config, state, mapping, reporting, SuccessFactors, and AD modules.
- `config`: sample tenant config and mapping config.
- `scripts/Register-SfAdSyncScheduledTask.ps1`: scheduled task bootstrap.
- `scripts/Get-SfAdSyncStatus.ps1`: summary view of the latest sync report and runtime state.
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

To view the current sync status from the configured state/report files:

```powershell
pwsh ./scripts/Get-SfAdSyncStatus.ps1 `
  -ConfigPath ./config/sample.sync-config.json
```

Use `-AsJson` if you want the status in machine-readable form.

## Notes
- The sample SuccessFactors entity and field names are placeholders and must be aligned to your tenant metadata before production use.
- Mapping source paths support indexed navigation syntax such as `employmentNav[0].jobInfoNav[0].jobTitle` for effective-dated or collection-backed OData expansions.
- The sample mapping config includes disabled Core HR examples for `title`, `division`, `employeeType`, and extension attributes. Enable them only after confirming your tenant payload and target AD attributes.
- OData v2 is the primary API path. Add `CompoundEmployee` only if tenant field coverage requires it.
- Current SAP references used for this implementation:
  - OData reference guide: `SF_HCM_OData_API_DEV.pdf`
  - SAP API reference guide for OData v2
  - CompoundEmployee guide for fallback coverage assessment
