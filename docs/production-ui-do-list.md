# Production UI Status

Last updated: 2026-05-14

This tracks the operator-facing UI against the current implementation. It is not
a production-readiness claim; the project remains alpha until deployment, access,
data-retention, monitoring, and recovery expectations are validated in the
target environment.

## Current Production Surfaces

- **Dashboard**: live runtime status, active run summary, schedule countdown,
  cancel action, dependency health dropdown, realtime SignalR updates, recent
  runs, run timeline, run mix chart, and bucket composition chart.
- **Sync**: ad hoc dry-run/live run queueing, cancellation, recurring schedule
  management, run history, dry-run/live-mode affordances, and development
  delete-all reset controls.
- **Exceptions**: unified triage queue for failed runs, manual review cases,
  conflicts, and guardrail failures, with search, paging, run links, decision
  links, and Worker 360 preview links.
- **Worker 360**: single-worker SuccessFactors lookup, directory match summary,
  decision tree, saved-preview fingerprint guardrail, preview comparison,
  preview history, apply readiness, grouped attribute diffs, source confidence,
  worker run history, and preview entries.
- **Run Detail**: run context, bucket counts, population comparison, filtered run
  entries, employment-status totals, changed-attribute totals, entry detail,
  decision tree, failure/manual-review diagnostics, JSON/JSONL/CSV export, and
  saved-preview links.
- **Lookup**: raw source lookup for operator/debug inspection.
- **Admin Users**: local break-glass user administration or SSO group visibility.
- **Admin Deletions**: graveyard deletion queue with pending, held, approve
  delete, place hold, remove hold, search, and paging.
- **Admin Config**: effective non-secret configuration review for deployment,
  auth, SuccessFactors, AD, operations, safety, alerts, mappings, source badges,
  anchors, and copy helpers.

## Status

- [x] Exception queue for failed runs, manual review, conflicts, and guardrail failures.
- [x] Worker 360 view with source, directory match, decision tree, preview, apply guardrail, preview history, preview comparison, and run history.
- [x] Saved worker preview/apply flow backed by preview fingerprints and server-side live-write gates.
- [x] Graveyard and deletion workbench.
- [x] Run detail analytics for bucket mix, population comparison, changed attributes, failure reasons, diagnostics, and exports.
- [x] Diff grouping by identity, organization, lifecycle, routing, and access in Worker 360.
- [x] Production configuration review in Admin configuration.
- [x] Health probe controls backed by SQLite dashboard settings.
- [x] Live-write safety UI: hide live apply when dry-run-only is active and require acknowledgement plus saved preview fingerprint for Worker 360 apply.
- [ ] Health detail page with probe history and dependency latency. Partial: dashboard health dropdown exists.
- [ ] Saved operator filter presets. Partial: core navigation, themes, version banner, dry-run banner, pagination, and denser tables exist.
- [ ] Audit UI for security and operator actions. Partial: audit events write to the security audit log.
- [ ] Production readiness checklist that converts Admin config into pass/warn/fail deployment checks.
- [ ] More complete approval workflows for high-risk live Active Directory changes beyond manual-review buckets and graveyard deletion approval.

## Next Backlog

1. Add an **Audit** admin page.
   - Read the configured security audit log path.
   - Show event type, outcome, actor, timestamp, target, and source IP/user agent when available.
   - Surface integrity verification status from `SecurityAuditService.VerifyIntegrity`.
   - Add filters for event type, outcome, actor, and date range.
   - Keep raw fields expandable, not shown by default.

2. Add a **Health Detail** page.
   - Link from the dashboard connection health dropdown.
   - Persist or expose recent probe snapshots for SuccessFactors, Active Directory,
     Worker Service, and SQLite.
   - Show latency, status history, last success, last failure, and failure detail.
   - Separate transient degraded states from hard unhealthy states.

3. Add **Production Readiness** checks to Admin configuration.
   - Summarize live-write mode, dry-run override, auth mode, role mappings,
     schedule state, audit-log path, audit-integrity key, SQLite path, health probe
     state, and required AD/SF settings.
   - Use pass/warn/fail states and link each check to the relevant config section.
   - Keep secrets hidden.

4. Add **Saved Operator Views**.
   - Persist common filters for Exceptions and Run Detail.
   - Support quick chips such as "Manual review", "Guardrail failures",
     "Failed live runs", and "Terminations".
   - Keep defaults focused on the latest actionable run state.

5. Tighten **High-Risk Approval UX**.
   - Keep Worker 360 as the apply surface for single-worker manual approval.
   - Make unsupported manual-review cases explicit in the Exceptions queue.
   - Show why an item can or cannot be applied, including live-write state,
     stale preview fingerprint, review case type, and required acknowledgement.

6. Improve **Run Analytics**.
   - Add top failure reasons across recent runs.
   - Add top changed attributes across the selected run set.
   - Add drilldowns from dashboard cards into filtered Run Detail or Exceptions
     views.

7. Finish **Responsive Operator Polish**.
   - Preserve dense desktop tables.
   - Improve small-screen table scanning for long DN, OU, source path, and JSON
     values.
   - Keep navigation and environment/version signals visible without covering
     content.
