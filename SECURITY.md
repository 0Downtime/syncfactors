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

Set `SYNCFACTORS_SQLITE_PASSWORD` from a secure secret store to encrypt the runtime SQLite database with SQLCipher. The API, worker, and automation commands must use the same value. On first startup with a password, conversion retains the plaintext database and sidecars only while creating and validating the encrypted replacement. Successful validation deletes those plaintext artifacts; any export, replacement, or validation failure restores them before startup fails.

On Unix-like hosts, SyncFactors hardens created runtime directories to owner-only access and hardens runtime files to owner read/write. On Windows, the deployment scripts grant the restricted runtime identity read/execute on the install tree and Modify only on inheritance-protected `state`. Rollback `_backups` are protected for SYSTEM and Administrators only. Service registry keys are likewise protected for SYSTEM/Administrators because their Environment values can contain SQLCipher, audit, or PFX secrets. Each patch reapplies these ACLs before creating sensitive snapshots. Do not grant the runtime identity write access to deployed binaries, scripts, configuration, or rollback backups.

Production security audit entries require a stable `SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY` shared by the API and worker. Treat it as a durable secret for the full lifetime of `security-audit.jsonl`: preserve it across reinstalls and patches, back it up independently of the server, and restore the original key whenever audit state is restored. The Windows deployment scripts reject API/worker key mismatches, accidental rotation, and nonempty audit history with no recoverable key; they never silently replace the key. Unkeyed audit chains are not an approved production configuration.

## Authentication And Authorization

The API supports local break-glass, OIDC-only, and hybrid OIDC plus break-glass modes. Production requires an explicit `SYNCFACTORS__AUTH__MODE`; a missing or incomplete OIDC configuration fails before database initialization rather than falling back to local authentication. OIDC deployments must configure a confidential client secret and at least one viewer, operator, or admin group/app-role value. Local break-glass accounts live in SQLite and should be limited to emergency or local automation scenarios. A fresh local or hybrid deployment must bootstrap a break-glass administrator before startup can complete.

Windows service auth secrets use the same environment-first, service-account Credential Manager fallback as external-system secrets. Under the default prefix, store them as `SyncFactors/SYNCFACTORS__AUTH__OIDC__CLIENTSECRET` and `SyncFactors/SYNCFACTORS__AUTH__BOOTSTRAPADMIN__PASSWORD` in the runtime service identity's profile; never the deploy administrator's profile.

Windows service installation requires an explicit restricted credential. LocalSystem is blocked unless an operator uses the conspicuous exceptional override. Normal patch deployment preserves service definitions, validates the configured identity when supplied, and snapshots complete service environments before applying managed auth changes. When auth mode is explicitly updated, all OIDC role-group lists are authoritative—including empty lists—so a removed privileged group cannot remain stale.

Local login, logout, and cookie-authenticated minimal API mutations require the matching antiforgery cookie plus an `X-SyncFactors-Antiforgery` request header. Obtain a non-cacheable request token from `GET /api/session/antiforgery`; after login, obtain a fresh token before another mutation because the token is bound to the authenticated identity. Razor forms retain their framework antiforgery validation, and the OIDC callback is not treated as an application mutation.

Forwarded headers are disabled by default. Enable them only behind a known reverse proxy and configure at least one exact `SyncFactors:ForwardedHeaders:KnownProxies` IP or `KnownNetworks` CIDR. Never enable forwarding with an unrestricted proxy/network list.

Role expectations:

- Viewer can inspect the portal and read status.
- Operator can queue and inspect sync/preview workflows.
- Admin and BreakGlassAdmin can manage schedule, local users, deletion queues, and admin configuration views.

## Write Safety

The effective write gate is shared by the API and worker. `SyncFactors:Runtime:DryRunOnly=true` takes precedence over ordinary sync config, blocks live AD write endpoints, makes scheduled runs dry-run-only, removes live-write controls from the UI, and shows the persistent dry-run banner.

Production deployment readiness is cryptographically tied to the current rollout. Before any staging or install-root mutation, the deployer requires one root release manifest containing a full hexadecimal commit SHA. It generates a fresh nonce, writes it to both service environments, and sends it only over HTTPS pinned to the configured API certificate together with a worker-start lower bound and that expected full API/worker commit. Plaintext HTTP, skipped certificate validation, and a generic/legacy HTTP 200 are rejected. While verification runs, a missing commit marker keeps the worker heartbeat alive but suppresses all queue, schedule, graveyard, and outbound email work. Only a successful `{"status":"ready","attested":true}` response causes an atomic SHA-256(nonce) marker publish. The nonce and digest must never be logged or returned.

Before starting new binaries, Windows patch deployment stops both services and snapshots the SQLite database together with its WAL, SHM, and rollback-journal sidecars. Automatic rollback restores that consistent snapshot before restarting the previous binaries, preventing an older release from opening a database migrated to a newer schema. Retained rollback snapshots contain production identity data and require the same encryption, ACL, backup, and retention controls as the live runtime database.
