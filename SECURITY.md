# Security Policy

## Supported Use

This project is intended for controlled enterprise environments that integrate SAP SuccessFactors with on-premises Active Directory.

Before production use:
- review and customize all sample configuration
- supply secrets through environment variables, macOS Keychain, Windows Credential Manager, or another approved secure secret store
- validate attribute mappings against your tenant and directory schema
- test in a lab and dry-run environment first
- delegate only the required Active Directory permissions to the runtime identity
- enable `SyncFactors:Runtime:DryRunOnly` when a monitoring deployment must not expose live AD write actions

## Reporting A Vulnerability

Please do not open public issues for suspected vulnerabilities.

Report security issues privately to the repository owner through GitHub security advisories or by contacting the maintainer directly through GitHub.

When reporting, include:
- a short description of the issue
- affected versions or commit range
- reproduction steps or a proof of concept
- impact and any suggested mitigation

You should receive an initial response within a reasonable time after the report is reviewed.

## Secret Handling

This repository should never contain:
- real SuccessFactors credentials
- real Active Directory credentials
- tenant-specific exports with personal data
- production reports or runtime state files

Sample configuration must keep placeholder values only.

## Runtime State Protection

Runtime SQLite state, audit logs, and preview logs can contain identity data. Store the runtime directory on an encrypted volume with OS-level access limited to the SyncFactors service account and operators who need break-glass access.

On Unix-like hosts, SyncFactors hardens created runtime directories to owner-only access and hardens runtime files to owner read/write. On Windows, apply equivalent ACLs through deployment policy or the service account profile.

Security audit entries include an integrity hash chain. Set `SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY` from a secure secret store to use keyed HMAC-SHA256 entries; without it, entries use an unkeyed SHA-256 chain that still detects accidental corruption and simple edits but is weaker against an attacker who can rewrite the whole file.

## Authentication And Authorization

The API supports local break-glass, OIDC-only, and hybrid OIDC plus break-glass modes. OIDC deployments must configure at least one viewer, operator, or admin group so access is explicitly mapped from tenant group membership. Local break-glass accounts live in SQLite and should be limited to emergency or local automation scenarios.

Role expectations:

- Viewer can inspect the portal and read status.
- Operator can queue and inspect sync/preview workflows.
- Admin and BreakGlassAdmin can manage schedule, local users, deletion queues, and admin configuration views.

## Write Safety

The effective write gate is shared by the API and worker. `SyncFactors:Runtime:DryRunOnly=true` takes precedence over ordinary sync config, blocks live AD write endpoints, makes scheduled runs dry-run-only, removes live-write controls from the UI, and shows the persistent dry-run banner.
