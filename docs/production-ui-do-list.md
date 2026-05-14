# Production UI Do List

Last updated: 2026-05-14

This tracker is for operator-facing production UI gaps. It should reflect what is
actually available in the Razor dashboard today, not long-range product ideas.

## Current Production Surfaces

- **Dashboard**: live runtime status, schedule countdown, cancel action, dependency
  health dropdown, recent run table, run timeline, run mix chart, and bucket
  composition chart.
- **Sync**: operator run controls, dry-run/live-mode affordances, and recent run
  summaries.
- **Exceptions**: unified triage queue for failed runs, manual review cases,
  conflicts, and guardrail failures, with search, paging, run links, decision
  links, and Worker 360 preview links.
- **Worker 360**: single-worker SuccessFactors lookup, directory match summary,
  decision tree, apply readiness, saved-preview fingerprint guardrail, preview
  comparison, preview history, grouped attribute diffs, source confidence, and
  worker run history.
- **Run Detail**: run context, bucket counts, population comparison, filtered run
  entries, employment-status totals, changed-attribute totals, entry detail,
  decision tree, failure/manual-review diagnostics, JSON/JSONL/CSV export, and
  saved-preview links.
- **Lookup**: raw source lookup for operator/debug inspection.
- **Admin Users**: local user administration.
- **Admin Deletions**: graveyard deletion queue with pending, held, approve delete,
  place hold, remove hold, search, and paging.
- **Admin Config**: effective non-secret configuration review, source badges,
  mapping coverage, anchors, and copy helpers.

## Status

- [x] Exception queue for failed runs, manual review, conflicts, and guardrail failures.
- [x] Worker 360 view with source, directory match, preview, apply guardrail, preview history, and run history.
- [x] Graveyard and deletion workbench.
- [x] Run detail analytics for bucket mix, changed attributes, failure reasons, diagnostics, and exports.
- [x] Diff grouping by identity, organization, lifecycle, routing, and access in Worker 360.
- [x] Production configuration review in Admin configuration.
- [x] Live-write safety UI: hide live apply when dry-run-only is active and require acknowledgement plus saved preview fingerprint for Worker 360 apply.
- [ ] Health detail page with probe history and dependency latency. Partial: dashboard health dropdown exists.
- [ ] Saved operator filter presets. Partial: core navigation, themes, version banner, dry-run banner, pagination, and denser tables exist.
- [ ] Audit UI for security and operator actions.
- [ ] Production readiness checklist that converts Admin config into pass/warn/fail deployment checks.

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
