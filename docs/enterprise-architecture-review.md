# SyncFactors Decision Justification Brief

Prepared for enterprise architecture review. Microsoft Entra references were reviewed on 2026-06-18. Commercial pricing references were reviewed on 2026-06-19.

## Executive Summary

We have selected SyncFactors as the synchronization approach for SAP SuccessFactors to Microsoft Active Directory. The Microsoft Entra user provisioning service was evaluated as the out-of-box option, but it does not provide several controls required for this environment. SyncFactors was built to close those gaps while keeping SuccessFactors as the authoritative HR source.

This decision should be treated as an interim, budget-conscious stopgap rather than a permanent enterprise IAM target state. SyncFactors provides the controls needed now while the organization plans and funds a full lifecycle management solution.

SyncFactors is a local-first tool for synchronizing SAP SuccessFactors HR worker data into Microsoft Active Directory. It supports joiner, mover, leaver, prehire, leave, rehire, and inactive-worker lifecycle workflows with explicit operator review, dry-run validation, auditability, and configurable safety controls before directory changes are applied.

The Microsoft Entra OOB service remains a strong baseline for standard cloud-managed HR-driven provisioning. It covers core account create, update, enable, disable, rehire, scheduled sync, attribute mapping, scoping filters, provisioning logs, and optional email writeback. We are not using it as the primary tool because the required operating model needs capabilities that are not first-class in the OOB service: local-first control, review-first execution, production dry-run enforcement, saved per-worker previews, exception queues, explicit guardrails for high-risk actions, lifecycle-specific OU routing, pre-hire AD provisioning, licensing group coordination, graveyard retention handling, VIP/protected-user deletion holds, deterministic lifecycle simulation, and inspectable local runtime state.

## Decision

Proceed with SyncFactors as the synchronization tool for SuccessFactors-to-Active Directory provisioning.

Decision rationale:

- SyncFactors meets the required review-first operating model before live directory writes.
- SyncFactors can enforce dry-run-only production mode at the deployment level.
- SyncFactors provides saved per-worker previews and explicit apply behavior.
- SyncFactors supports lifecycle-specific OU decisions for active, prehire, leave, and graveyard states.
- SyncFactors can provision pre-hires into AD before their official hire date so access and downstream preparation can happen ahead of day one.
- SyncFactors provides guardrails, exception queues, manual review buckets, and deletion queue handling for high-risk actions.
- SyncFactors supports licensing group assignment as part of the AD provisioning path.
- SyncFactors supports graveyard retention and an automatic deletion queue, with hold capability for VIPs or other users who must be retained beyond the standard deletion window.
- SyncFactors keeps runtime state, logs, security audit events, dependency health, and run history locally inspectable.
- SyncFactors supports deterministic mock SuccessFactors, fixture playback, lifecycle simulation, and AD test automation for pre-production validation.
- SyncFactors supports SuccessFactors OAuth client credentials in addition to Basic authentication, which provides a path if security policy disallows Basic authentication.
- SyncFactors handles identity edge cases that the OOB connector does not support natively, including duplicate names, controlled name normalization, and CWK-to-FTE transitions where SuccessFactors issues a new employee identifier but AD continuity must be retained.
- SyncFactors serves as a stopgap until budget is available for a broader commercial lifecycle management platform.

## Tool Purpose

SyncFactors provides an operator-controlled synchronization layer between SuccessFactors Employee Central and Active Directory.

Primary objectives:

- Use SuccessFactors as the authoritative source for worker identity and employment lifecycle state.
- Create, update, enable, disable, move, and optionally delete Active Directory accounts according to configured lifecycle policy.
- Keep dry-run, preview, approval, audit, and rollback-oriented workflows central to operations.
- Give operators a local portal for dashboarding, run history, health, preview/apply, exception handling, Worker 360 lookup, deletion queue review, user access, and configuration visibility.
- Support controlled Windows deployment as API and worker services with SQLite runtime state, local logs, secure secret resolution, and AD least-privilege delegation.

## High-Level Requirements

### Functional Requirements

- Source worker data from SuccessFactors OData.
- Support full sync and delta-preferred sync patterns.
- Map confirmed SuccessFactors fields to AD attributes, including required identity, name, UPN, mail, department, division, company, location, title, and office address fields.
- Support configurable mapping for optional extension attributes such as business unit, cost center, employee class, region, people group, leadership level, bargaining unit, union job code, and hire date.
- Generate name-based email and UPN values while resolving duplicate local-parts by appending a number, such as `first.last2`, when the preferred value is already in use.
- Support tenant-controlled name normalization before account naming, including cleanup of suffixes or title noise such as Junior, Senior, Jr., or Sr. where the tenant naming standard requires it.
- Resolve worker lifecycle state into active, prehire, leave, inactive, and graveyard outcomes.
- Route users to configured Active, Prehire, Leave, and Graveyard OUs.
- Provision prehire users into AD before their hire date using the configured prehire window and prehire OU.
- Disable leave/inactive workers according to configured employment status values.
- Avoid creating missing AD accounts for inactive or terminated workers.
- Correlate identity transitions, including contractor-to-FTE conversion scenarios where SuccessFactors issues a new `personIdExternal` or employee identifier while AD must retain the prior account relationship.
- Add configured licensing groups during AD create/update workflows when licensing assignment is required.
- Preserve manager handling through distinguished-name resolution rather than writing raw manager IDs directly into AD.
- Optionally write the generated AD mail value back to SuccessFactors after successful live AD mutation.

### Control And Safety Requirements

- Run in dry-run mode before live writes.
- Enforce deployment-level dry-run-only mode when required.
- Persist previews and require explicit apply from a saved worker fingerprint.
- Queue, cancel, and track runs.
- Apply guardrails for high-volume creates, disables, and deletions.
- Route high-risk disable, delete, and graveyard actions to manual review when policy requires it.
- Maintain an exception queue for failed runs, manual review items, conflicts, and guardrail failures.
- Maintain a deletion queue for graveyard users, including pending and on-hold states.
- Support retention holds for VIPs, executives, legal-hold users, or other accounts that must not be automatically deleted.

### Security Requirements

- Use LDAPS by default for AD, with certificate validation and optional thumbprint trust.
- Support binding to AD through the Windows service identity for least-privilege delegation.
- Keep tracked configuration free of secrets.
- Resolve secrets from environment variables and platform secure stores, including Windows Credential Manager for service deployments.
- Support local break-glass auth, OIDC-only auth, and hybrid OIDC plus break-glass modes.
- Use role boundaries for Viewer, Operator, Admin, and BreakGlassAdmin.
- Emit security audit events with integrity chaining.
- Support SQLite encryption for Windows service deployments.

### Operational Requirements

- Provide a local operator dashboard with runtime status, recent runs, active run progress, dependency health, and realtime updates.
- Provide run detail, worker detail, lookup, exceptions, deletion queue, user access, and configuration views.
- Record worker heartbeat and dependency health.
- Support recurring full-sync scheduling.
- Support local rolling logs, run-scoped logs when enabled, and optional Application Insights telemetry.
- Provide mock SuccessFactors, fixture playback, lifecycle simulation, and end-to-end automation for pre-production validation.

## OOB Microsoft Entra Provisioning Baseline

Microsoft Entra user provisioning service integrates with SuccessFactors Employee Central to manage the identity lifecycle of users. Microsoft documents three prebuilt SuccessFactors integrations:

- SuccessFactors to on-premises Active Directory user provisioning.
- SuccessFactors to Microsoft Entra user provisioning.
- SuccessFactors Writeback.

For the AD flow, Microsoft describes a cloud-managed provisioning service that reads SuccessFactors Employee Central changes, sends create/update/enable/disable requests to the on-premises Microsoft Entra Connect Provisioning Agent, and uses that agent to apply changes in AD. Microsoft documents support for hiring, employee attribute/profile updates, terminations, and rehires. It also supports attribute mappings, expressions, scoping filters, target object action filtering, on-demand single-user testing, provisioning logs, and quarantine email notification.

Microsoft's SuccessFactors integration primarily retrieves worker data through the `PerPerson` OData endpoint and expands related entities based on configured mappings. The documented OOB model includes common entities such as `PerPersonal`, `PerPhone`, `PerEmail`, `EmpEmployment`, `User`, `EmpJob`, manager, company, department, business unit, cost center, division, job code, location, pay grade, event reason, global assignment, employment type, employee class, and employment status picklists.

## OOB Fit And Selection Outcome

The OOB Microsoft Entra service is a good fit when the required architecture is:

- Cloud-managed provisioning with Microsoft Entra as the orchestration layer.
- Standard SuccessFactors-to-AD or SuccessFactors-to-Entra identity lifecycle.
- Basic create, update, enable, disable, and rehire behavior.
- Standard attribute mapping, expression transformation, and scoping filters.
- Scheduled sync with Microsoft provisioning logs and audit summary.
- Optional email writeback to SuccessFactors through the OOB writeback app.
- Operation through the Microsoft Entra admin center and the on-premises provisioning agent.

Those strengths do not cover the full operating model required here. The OOB service was not selected because it does not provide sufficient local control, dry-run enforcement, preview/apply workflow, lifecycle-specific retention handling, and operator review controls for the planned production process.

## Commercial Alternatives And Pricing

The following commercial alternatives were reviewed to show that the decision was not limited to only Microsoft Entra OOB provisioning versus a custom tool. Pricing is based on public vendor pages available on 2026-06-19 and uses 4,000 users for simple list-price math. Taxes, enterprise discounts, implementation services, premium support, connector fees, and data-volume charges are not included.

| Commercial option | Public pricing basis | Approximate 4,000-user pricing | Assessment for this decision |
| --- | --- | --- | --- |
| Okta Workforce Identity Essentials | Okta lists Essentials at `$17/user/month`, paid yearly, and includes Lifecycle Management, Access Governance, and 50 Workflows. | `$68,000/month`; approximately `$816,000/year` before discounts or services. | Strong commercial IAM platform. Would still require design and workflow work for tenant-specific duplicate handling, suffix cleanup, CWK-to-FTE identity continuity, prehire OU behavior, VIP deletion holds, and local review-first AD execution. |
| JumpCloud User Lifecycle Management plus Cloud Directory | JumpCloud lists Cloud Directory at `$3/user/month` and User Lifecycle Management at `$3/user/month`, billed annually. | Minimum public module math: `$24,000/month`; approximately `$288,000/year`. Adding SSO at another `$3/user/month` raises estimate to `$36,000/month`; approximately `$432,000/year`. Enterprise packaging may require sales quote. | Viable commercial identity lifecycle option with HRIS integration capabilities. Less direct fit for the current AD-centered control model unless validated for SuccessFactors-specific extraction, AD OU routing, licensing groups, deletion holds, and CWK-to-FTE continuity. |
| Microsoft Entra ID Governance | Microsoft lists Entra ID Governance at `$7/user/month`, paid yearly, and states it is available for Entra ID P1/P2 customers. | `$28,000/month`; approximately `$336,000/year`, plus any required P1/P2 licensing not already owned. | Useful adjacent Microsoft governance capability, but not a replacement for the required SuccessFactors-to-AD synchronization control plane. It does not remove the OOB provisioning gaps that drove SyncFactors selection. |
| Boomi Enterprise Platform / Workato iPaaS | Public pages describe subscription, pay-as-you-go, and sales-led pricing models. Boomi lists pay-as-you-go starting at `$99/month plus usage`; Workato directs buyers to sales/demo rather than publishing per-user pricing. | Not reliably calculable from public pricing for 4,000 users because cost depends on connectors, recipes/processes, environments, transaction volume, support tier, and services. Vendor quote required. | Flexible integration platforms could orchestrate HR-to-directory workflows, but would introduce platform licensing, implementation, support, and custom process ownership while still requiring tenant-specific logic for the same edge cases SyncFactors already implements. |

Cost conclusion: publicly priced alternatives range from roughly `$288,000/year` to `$816,000/year` before implementation and support, and quote-based iPaaS options cannot be credibly priced without a vendor proposal. The commercial alternatives may be useful if the organization wants to buy a broader IAM or integration platform, but they do not materially weaken the SyncFactors decision because the differentiators are tenant-specific lifecycle control, local review-first execution, dry-run enforcement, and AD-specific operational guardrails. SyncFactors should therefore be positioned as a controlled interim solution until budget is available for a full lifecycle management platform.

## OOB Gaps And SyncFactors Justification

| Requirement area | OOB Microsoft Entra capability | Why SyncFactors was selected |
| --- | --- | --- |
| Review-first operations | OOB supports on-demand provisioning for a single user, scoping filters, provisioning logs, and gradual rollout guidance. | SyncFactors supports dry-run-first full/cohort execution, saved per-worker previews, explicit apply from saved fingerprint, and operator review before selected changes are applied. |
| Production write control | OOB guidance recommends testing a few users before expanding scope. | SyncFactors enforces deployment-level dry-run-only mode that blocks live writes in both API and worker paths and removes live-write controls from the UI. |
| Manual approval and exception workbench | OOB logs actions and can notify when a provisioning job enters quarantine. | SyncFactors provides manual review buckets, guardrail breach handling, conflict handling, exception queues, and high-risk disable/delete/graveyard move review. |
| Lifecycle-specific OU policy | OOB can set a default AD container and can map `parentDistinguishedName`; if it is not configured, the default container only applies to creates. | SyncFactors implements explicit Active, Prehire, Leave, and Graveyard OU policy, leave/inactive precedence, refusal to create missing inactive users, and status-code-driven routing. |
| Pre-hire provisioning before start date | OOB supports prehire scenarios within documented connector behavior, but tenant-specific lead-time handling and prehire OU routing are limited to connector configuration patterns. | SyncFactors explicitly includes due prehires based on the configured lead window, creates enabled prehire AD accounts, and routes them to the configured Prehire OU until activation. |
| Licensing group assignment | OOB provisioning can map attributes and group-related behavior through the Microsoft Entra provisioning model, but tenant-specific AD licensing group assignment is not a focused operator workflow. | SyncFactors supports configured AD licensing groups during directory mutation so account provisioning and license-related group membership can be coordinated in the same controlled run. |
| Deletion and retention workflow | OOB target actions can include create/update and can disable/delete users that go out of scope unless configured otherwise. | SyncFactors provides graveyard retention reporting, deletion queue states, on-hold handling, optional auto-delete from graveyard, and retention holds for VIP or otherwise protected accounts. |
| Local operational control | OOB uses Microsoft Entra cloud provisioning plus the on-premises provisioning agent. | SyncFactors provides local-first runtime state, local operator UI, inspectable SQLite state, local logs, and local Windows service deployment. |
| Duplicate name and email handling | OOB attribute mapping can generate account values, but collision-specific naming policy is limited to the OOB provisioning behavior and configured expressions. | SyncFactors checks generated UPN/mail/proxy values and appends numeric suffixes for duplicate name-based local-parts, avoiding failed or ambiguous creates when two workers share the same preferred name. |
| Name cleanup and tenant naming policy | OOB mappings can transform attributes, but tenant-specific suffix cleanup and exception handling can be difficult to enforce consistently. | SyncFactors can apply tenant-controlled normalization before generating AD names and addresses, including stripping suffix/title noise such as Junior, Senior, Jr., or Sr. where required by naming standards. |
| SuccessFactors authentication | Microsoft docs state the SuccessFactors provisioning service uses basic authentication to connect to Employee Central OData APIs. | SyncFactors supports Basic and OAuth client credentials for SuccessFactors, giving the architecture a path if Basic authentication is not acceptable. |
| Tenant-specific data extraction | OOB ships with 90+ predefined SuccessFactors attributes and supports JSONPath additions. | SyncFactors uses tenant-confirmed `EmpJob` and `PerPerson` preview shapes with normalized source keys and explicit AD target decisions. |
| CWK-to-FTE identity continuity | OOB provisioning generally treats identity anchor changes as separate source identities and does not natively model a tenant-specific contractor-to-employee handoff that issues a new SuccessFactors employee ID while preserving the AD account relationship. | SyncFactors supports configured identity correlation attributes for successor and previous `personIdExternal` values, allowing the AD-side relationship to be retained while the SuccessFactors-side employee identifier changes. |
| Prehire, rehire, conversion edge cases | Microsoft documents prehire prerequisites and notes a known limitation where order cannot be deterministically determined for some rehire or conversion prehires. | SyncFactors includes deterministic lifecycle simulation and explicit prehire OU handling so edge cases can be validated before production writes. |
| Termination edge cases | Microsoft documents known issues around default account-status handling, including possible disable one day prior to termination and cases where null termination info prevents AD disable. | SyncFactors uses configurable inactive and leave status values with explicit lifecycle policy aligned to tenant-specific status fields and timing rules. |
| Observability and audit | OOB provides provisioning logs, audit summary, progress, and quarantine notification. | SyncFactors provides a local dashboard, dependency probes, worker heartbeat, run history, per-run detail, optional run-scoped logs, and hash-chained security audit events. |
| Testability | OOB supports testing mappings with on-demand provisioning and scoped rollout. | SyncFactors includes mock SuccessFactors, fixture generation/playback, lifecycle simulation, and E2E automation against AD test OUs. |

## SyncFactors Implementation Capabilities

The current implementation provides the capabilities that drove the decision:

- Local-first .NET API/operator portal plus background worker.
- SQLite-backed runtime state for runs, run entries, queue, schedules, runtime status, worker heartbeat, local users, OIDC accounts, delta sync state, dashboard settings, and graveyard retention.
- SuccessFactors OData source with primary `EmpJob` query and `PerPerson` preview query shapes.
- Configurable SuccessFactors Basic or OAuth client-credentials authentication.
- AD integration through isolated lookup and command gateways.
- Name-based email local-part generation with AD availability checks and numeric suffixing for collisions.
- In-run duplicate email reservation so two creates in the same run cannot reserve the same UPN/mail value.
- Configurable identity-correlation attributes for successor and previous `personIdExternal` values, supporting CWK-to-FTE transitions and other source-ID handoffs.
- LDAPS default transport, certificate validation, optional signing/sealing, and Windows service identity binding when explicit AD credentials are not configured.
- Configurable AD OUs for active, prehire, leave, and graveyard states.
- Prehire lead-window support through `sync.enableBeforeStartDays`, allowing AD accounts to be created before the hire date and held in the Prehire OU.
- Configured AD licensing group support during directory mutations.
- Dry-run and live sync queueing, cancellation, recurring schedule configuration, and production dry-run-only override.
- Worker preview/apply with saved preview fingerprint.
- Worker 360 view with source summary, directory match, decision tree, preview history, comparison, apply readiness, attribute diff, source confidence, run history, and preview entries.
- Exceptions queue for failed runs, manual review, conflicts, and guardrail failures.
- Admin deletion queue for graveyard users.
- Graveyard retention reporting, automatic deletion coordination, and reversible deletion holds for VIP or protected users.
- Security audit JSONL with integrity chaining.
- Local break-glass, OIDC-only, and hybrid auth modes.
- Mock SuccessFactors, fixture playback, lifecycle simulator, and production readiness automation.

## Architecture Review Focus Areas

Enterprise architecture review should validate the implementation and operating controls for the selected SyncFactors path:

- Confirm the required AD delegation model for the runtime account and the OUs/groups in scope.
- Confirm SuccessFactors status fields and values for active, prehire, leave, inactive, terminated, rehire, and conversion states.
- Confirm prehire lead time, prehire OU routing, and day-one activation behavior.
- Confirm day-one AD attribute mappings and optional extension attributes that can be phased later.
- Confirm AD licensing groups that should be assigned during provisioning and any conditions for assignment.
- Confirm naming policy for duplicate names, suffix cleanup, and whether Junior/Senior/Jr./Sr. should be removed before UPN/mail generation.
- Confirm CWK-to-FTE handoff rules, including which AD attributes store successor and previous SuccessFactors identifiers.
- Confirm whether SuccessFactors OAuth client credentials are required by security policy.
- Confirm whether email-only writeback is sufficient or whether phone writeback is required in a later phase.
- Confirm audit retention, log retention, SQLite encryption, backup, and disaster recovery requirements.
- Confirm VIP/legal-hold retention rules and who may place or remove deletion holds.
- Confirm the production readiness gate before moving from dry-run-only to live writes.
- Confirm operational ownership for exception review, deletion queue review, scheduler changes, and production releases.

## Governance And Risk Treatment

SyncFactors is a custom-built control plane and should move to production only after formal readiness review. The current decision is to proceed with SyncFactors because it satisfies required controls that the OOB service does not provide. That decision should be paired with governance controls:

- Keep production in dry-run-only mode until readiness criteria are satisfied.
- Require documented approval before enabling live writes.
- Validate the tenant field mapping and lifecycle status matrix using sanitized SuccessFactors exports.
- Validate prehire provisioning and activation scenarios before go-live.
- Validate licensing group assignment behavior in AD test OUs.
- Validate duplicate-name and suffix-cleanup examples against tenant naming standards.
- Validate contractor-to-FTE identity-correlation scenarios in non-production before go-live.
- Validate VIP/protected-user deletion holds so held users remain out of automatic deletion.
- Validate AD delegation against least-privilege requirements.
- Run E2E automation against AD test OUs before production cutover.
- Review run history, exception handling, and security audit output as part of go-live evidence.
- Maintain rollback and retention procedures for graveyard/deletion workflows.
- Revisit the decision when budget is available for a full lifecycle management solution, using SyncFactors run data and exception history as input to commercial platform requirements.

## Decision Summary

SyncFactors is the selected path because it provides required capabilities that the Microsoft Entra OOB provisioning service does not provide as first-class behaviors for this environment:

- Local-first execution and local operator control.
- Full dry-run and production dry-run-only enforcement.
- Saved per-worker previews and explicit apply workflows.
- Manual review and exception handling for high-risk changes.
- Lifecycle-specific OU routing and graveyard retention handling.
- Pre-hire AD provisioning before official hire date.
- Licensing group assignment as part of controlled AD provisioning.
- Automatic deletion queue with VIP/protected-user retention holds.
- Tenant-specific SuccessFactors query and mapping behavior.
- Duplicate name/email collision handling with numeric suffixing.
- Tenant-specific name normalization and suffix cleanup.
- Contractor-to-FTE identity continuity through configured successor/previous identifier correlation.
- Hash-chained security audit events and inspectable runtime state.
- Deterministic test automation for lifecycle and AD scenarios.

The OOB Microsoft Entra service remains a valid reference architecture for standard cloud-managed provisioning, but it was not selected because it does not meet the required control, review, and operational validation model. SyncFactors should be reviewed periodically as a stopgap and replaced or absorbed when budget supports a full enterprise lifecycle management solution.

## References

- Microsoft: How Microsoft Entra provisioning integrates with SAP SuccessFactors - https://learn.microsoft.com/en-us/entra/identity/app-provisioning/sap-successfactors-integration-reference
- Microsoft: Configure SAP SuccessFactors to Active Directory user provisioning - https://learn.microsoft.com/en-us/entra/identity/saas-apps/sap-successfactors-inbound-provisioning-tutorial
- Microsoft: SAP SuccessFactors attribute reference for Microsoft Entra ID - https://learn.microsoft.com/en-us/entra/identity/app-provisioning/sap-successfactors-attribute-reference
- Microsoft: Microsoft Entra plans and pricing - https://www.microsoft.com/en-us/security/business/microsoft-entra-pricing
- Okta: Plans and pricing - https://www.okta.com/pricing/
- JumpCloud: Pricing - https://jumpcloud.com/pricing
- JumpCloud: HRIS Integration - https://jumpcloud.com/platform/hris-integration
- Boomi: Enterprise Platform pricing and editions - https://boomi.com/pricing/
- Workato: Pricing model - https://www.workato.com/pricing
- Local: README - ../README.md
- Local: Current Architecture - architecture.md
- Local: EmpJob To AD Mapping - empjob-ad-mapping.md
- Local: OU Decision Tree - ou-decision-tree.md
- Local: Sample real SuccessFactors to real AD config - ../config/sample.real-successfactors.real-ad.sync-config.json
- Local: Tenant-confirmed EmpJob mapping config - ../config/sample.empjob-confirmed.mapping-config.json
