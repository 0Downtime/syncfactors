# Security Policy

## Supported Use

This project is intended for controlled enterprise environments that integrate SAP SuccessFactors with on-premises Active Directory.

Before production use:
- review and customize all sample configuration
- supply secrets through environment variables or another secure secret store
- validate attribute mappings against your tenant and directory schema
- test in a lab or dry-run environment first

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
