import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { afterEach, describe, expect, it } from 'vitest';
import { ReportService } from './report-service.js';
import type { DashboardStatus } from './types.js';

const tempPaths: string[] = [];
const NON_WINDOWS_AD_PROBE_WARNING = 'Active Directory health probe is skipped on non-Windows hosts for the web dashboard.';
const execFileAsync = promisify(execFile);

async function writeReport(directory: string, fileName: string, report: Record<string, unknown>) {
  await fs.mkdir(directory, { recursive: true });
  const reportPath = path.join(directory, fileName);
  await fs.writeFile(reportPath, JSON.stringify(report));
  return reportPath;
}

async function createStatusFixture() {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'syncfactors-web-'));
  tempPaths.push(root);
  const reportDirectory = path.join(root, 'reports');
  const reviewDirectory = path.join(reportDirectory, 'review');

  const run1Path = await writeReport(reportDirectory, 'syncfactors-Delta-20260320-100000.json', {
    runId: 'run-1',
    artifactType: 'WorkerPreview',
    mode: 'Review',
    dryRun: true,
    status: 'Succeeded',
    startedAt: '2026-03-20T10:00:00Z',
    completedAt: '2026-03-20T10:05:00Z',
    operations: [
      {
        workerId: '1001',
        bucket: 'updates',
        operationType: 'UpdateAttributes',
        target: { samAccountName: 'jdoe' },
        before: { department: 'Finance' },
        after: { department: 'Sales' },
      },
    ],
    updates: [
      {
        workerId: '1001',
        samAccountName: 'jdoe',
        reason: 'AttributeDelta',
        reviewCategory: 'ExistingUserChanges',
        reviewCaseType: 'RehireCase',
        operatorActionSummary: 'Review before retry.',
        operatorActions: [{ label: 'Confirm account reuse', description: 'Reuse the former account.' }],
        changedAttributeDetails: [
          {
            sourceField: 'department',
            targetAttribute: 'department',
            currentAdValue: 'Finance',
            proposedValue: 'Sales',
          },
        ],
      },
    ],
    manualReview: [],
    quarantined: [],
    conflicts: [],
    guardrailFailures: [],
    creates: [],
    enables: [],
    disables: [],
    graveyardMoves: [],
    deletions: [],
    unchanged: [],
  });

  await writeReport(reviewDirectory, 'syncfactors-Review-20260320-110000.json', {
    runId: 'run-2',
    artifactType: 'FirstSyncReview',
    mode: 'Review',
    dryRun: true,
    status: 'Succeeded',
    startedAt: '2026-03-20T11:00:00Z',
    completedAt: '2026-03-20T11:03:00Z',
    operations: [],
    updates: [],
    manualReview: [
      {
        workerId: '1001',
        reason: 'RehireDetected',
        reviewCaseType: 'RehireCase',
      },
    ],
    quarantined: [
      {
        workerId: '1002',
        reason: 'ManagerNotResolved',
        reviewCaseType: 'UnresolvedManager',
      },
    ],
    conflicts: [],
    guardrailFailures: [],
    creates: [],
    enables: [],
    disables: [],
    graveyardMoves: [],
    deletions: [],
    unchanged: [],
  });

  await fs.writeFile(path.join(reportDirectory, 'syncfactors-bad.json'), '{bad-json');

  const status: DashboardStatus = {
    configPath: path.join(root, 'config.json'),
    latestRun: {
      runId: 'run-1',
      path: run1Path,
      artifactType: 'WorkerPreview',
      mode: 'Review',
      dryRun: true,
      status: 'Succeeded',
      startedAt: '2026-03-20T10:00:00Z',
      completedAt: '2026-03-20T10:05:00Z',
      durationSeconds: 300,
      reversibleOperations: 1,
      creates: 0,
      updates: 1,
      enables: 0,
      disables: 0,
      graveyardMoves: 0,
      deletions: 0,
      quarantined: 0,
      conflicts: 0,
      guardrailFailures: 0,
      manualReview: 0,
      unchanged: 0,
    },
    currentRun: {},
    recentRuns: [],
    summary: {
      lastCheckpoint: '2026-03-20T09:00:00Z',
      totalTrackedWorkers: 1,
      suppressedWorkers: 1,
      pendingDeletionWorkers: 0,
    },
    health: {
      successFactors: { status: 'OK', detail: 'oauth' },
      activeDirectory: { status: 'OK', detail: 'dc01' },
    },
    trackedWorkers: [
      {
        workerId: '1001',
        distinguishedName: 'CN=Jamie Doe,OU=Employees,DC=example,DC=com',
        suppressed: true,
        deleteAfter: '2026-03-30T10:00:00Z',
        lastSeenStatus: 'active',
      },
    ],
    context: {},
    paths: {
      configPath: path.join(root, 'config.json'),
      statePath: path.join(root, 'state.json'),
      reportDirectory,
      reviewReportDirectory: reviewDirectory,
      reportDirectories: [reportDirectory, reviewDirectory],
      runtimeStatusPath: path.join(root, 'runtime-status.json'),
    },
    warnings: [],
  };

  return status;
}

afterEach(async () => {
  await Promise.all(tempPaths.splice(0).map((tempPath) => fs.rm(tempPath, { recursive: true, force: true })));
});

describe('ReportService', () => {
  it('normalizes entries, queues, worker detail, and malformed report warnings', async () => {
    const status = await createStatusFixture();
    const service = new ReportService();

    const runs = await service.listRuns(status, {});
    const detail = await service.getRun(status, 'run-1');
    const entries = await service.getRunEntries(status, 'run-1', { bucket: 'updates' });
    const queue = await service.getQueue(status, 'manual-review', {});
    const worker = await service.getWorkerDetail(status, '1001');

    expect(runs.total).toBe(2);
    expect(runs.warnings[0]).toMatch(/Skipped malformed report/);
    expect(detail.reviewExplorer.changed).toBe(1);
    expect(entries.entries[0].entryId).toBe('run-1:updates:1001:0');
    expect(entries.entries[0].diffRows[0]?.attribute).toBe('department');
    expect(entries.entries[0].operationSummary?.action).toMatch(/Update attributes/);
    expect(queue.reasonGroups[0]?.label).toBe('RehireDetected');
    expect(worker.trackedWorker?.suppressed).toBe(true);
    expect(worker.latestEntry?.workerId).toBe('1001');
  });

  it('synthesizes enabled diff rows for disable entries without explicit attribute rows', async () => {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'syncfactors-web-disable-'));
    tempPaths.push(root);
    const reportDirectory = path.join(root, 'reports');
    const reviewDirectory = path.join(reportDirectory, 'review');

    const runPath = await writeReport(reviewDirectory, 'syncfactors-Review-20260323-170000.json', {
      runId: 'run-disable-1',
      artifactType: 'WorkerPreview',
      mode: 'Review',
      dryRun: true,
      status: 'Succeeded',
      startedAt: '2026-03-23T17:00:00Z',
      completedAt: '2026-03-23T17:00:03Z',
      operations: [],
      updates: [],
      manualReview: [],
      quarantined: [],
      conflicts: [],
      guardrailFailures: [],
      creates: [],
      enables: [],
      disables: [
        {
          workerId: '40618',
          samAccountName: '40618',
          currentEnabled: true,
          proposedEnable: false,
          reviewCategory: 'ExistingUserOffboarding',
        },
      ],
      graveyardMoves: [],
      deletions: [],
      unchanged: [],
    });

    const status: DashboardStatus = {
      configPath: path.join(root, 'config.json'),
      latestRun: {
        runId: 'run-disable-1',
        path: runPath,
        artifactType: 'WorkerPreview',
        mode: 'Review',
        dryRun: true,
        status: 'Succeeded',
        startedAt: '2026-03-23T17:00:00Z',
        completedAt: '2026-03-23T17:00:03Z',
        durationSeconds: 3,
        reversibleOperations: 0,
        creates: 0,
        updates: 0,
        enables: 0,
        disables: 1,
        graveyardMoves: 0,
        deletions: 0,
        quarantined: 0,
        conflicts: 0,
        guardrailFailures: 0,
        manualReview: 0,
        unchanged: 0,
      },
      currentRun: {},
      recentRuns: [],
      summary: {
        lastCheckpoint: null,
        totalTrackedWorkers: 1,
        suppressedWorkers: 0,
        pendingDeletionWorkers: 0,
      },
      health: {
        successFactors: { status: 'OK', detail: 'oauth' },
        activeDirectory: { status: 'OK', detail: 'dc01' },
      },
      trackedWorkers: [],
      context: {},
      paths: {
        configPath: path.join(root, 'config.json'),
        statePath: path.join(root, 'state.json'),
        reportDirectory,
        reviewReportDirectory: reviewDirectory,
        reportDirectories: [reportDirectory, reviewDirectory],
        runtimeStatusPath: path.join(root, 'runtime-status.json'),
      },
      warnings: [],
    };

    const service = new ReportService();
    const entries = await service.getRunEntries(status, 'run-disable-1', { workerId: '40618' });

    expect(entries.entries).toHaveLength(1);
    expect(entries.entries[0]?.diffRows).toEqual([
      {
        attribute: 'enabled',
        source: null,
        before: 'true',
        after: 'false',
        changed: true,
      },
    ]);
  });

  it('keeps the non-Windows AD probe warning out of run, queue, and worker responses', async () => {
    const status = await createStatusFixture();
    status.warnings = [NON_WINDOWS_AD_PROBE_WARNING];
    const service = new ReportService();

    const runs = await service.listRuns(status, {});
    const detail = await service.getRun(status, 'run-1');
    const entries = await service.getRunEntries(status, 'run-1', { bucket: 'updates' });
    const queue = await service.getQueue(status, 'manual-review', {});
    const worker = await service.getWorkerDetail(status, '1001');

    expect(runs.warnings).toContain(NON_WINDOWS_AD_PROBE_WARNING);
    expect(detail.warnings).not.toContain(NON_WINDOWS_AD_PROBE_WARNING);
    expect(entries.warnings).not.toContain(NON_WINDOWS_AD_PROBE_WARNING);
    expect(queue.warnings).not.toContain(NON_WINDOWS_AD_PROBE_WARNING);
    expect(worker.warnings).not.toContain(NON_WINDOWS_AD_PROBE_WARNING);
  });

  it('prefers SQLite-backed runs when a database is available', async () => {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'syncfactors-sqlite-'));
    tempPaths.push(root);
    const sqlitePath = path.join(root, 'syncfactors.db');
    const status = await createStatusFixture();
    status.paths.sqlitePath = sqlitePath;
    status.paths.statePath = path.join(root, 'state.json');
    status.recentRuns = [];

    const report = {
      runId: 'run-sqlite-1',
      artifactType: 'SyncReport',
      mode: 'Delta',
      dryRun: false,
      status: 'Succeeded',
      startedAt: '2026-03-21T10:00:00Z',
      completedAt: '2026-03-21T10:03:00Z',
      operations: [],
      creates: [{ workerId: '2001', samAccountName: 'adoe', targetOu: 'OU=Employees,DC=example,DC=com' }],
      updates: [],
      enables: [],
      disables: [],
      graveyardMoves: [],
      deletions: [],
      quarantined: [],
      conflicts: [],
      guardrailFailures: [],
      manualReview: [],
      unchanged: [],
    };

    const sql = `
PRAGMA journal_mode = WAL;
CREATE TABLE IF NOT EXISTS runs (
  run_id TEXT PRIMARY KEY,
  state_path TEXT NULL,
  path TEXT NULL,
  artifact_type TEXT NOT NULL,
  worker_scope_json TEXT NULL,
  config_path TEXT NULL,
  mapping_config_path TEXT NULL,
  mode TEXT NULL,
  dry_run INTEGER NOT NULL DEFAULT 0,
  status TEXT NULL,
  started_at TEXT NULL,
  completed_at TEXT NULL,
  duration_seconds INTEGER NULL,
  reversible_operations INTEGER NOT NULL DEFAULT 0,
  creates INTEGER NOT NULL DEFAULT 0,
  updates INTEGER NOT NULL DEFAULT 0,
  enables INTEGER NOT NULL DEFAULT 0,
  disables INTEGER NOT NULL DEFAULT 0,
  graveyard_moves INTEGER NOT NULL DEFAULT 0,
  deletions INTEGER NOT NULL DEFAULT 0,
  quarantined INTEGER NOT NULL DEFAULT 0,
  conflicts INTEGER NOT NULL DEFAULT 0,
  guardrail_failures INTEGER NOT NULL DEFAULT 0,
  manual_review INTEGER NOT NULL DEFAULT 0,
  unchanged INTEGER NOT NULL DEFAULT 0,
  review_summary_json TEXT NULL,
  report_json TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS run_entries (
  entry_id TEXT PRIMARY KEY,
  run_id TEXT NOT NULL,
  state_path TEXT NULL,
  bucket TEXT NOT NULL,
  bucket_index INTEGER NOT NULL,
  worker_id TEXT NULL,
  sam_account_name TEXT NULL,
  reason TEXT NULL,
  review_category TEXT NULL,
  review_case_type TEXT NULL,
  started_at TEXT NULL,
  item_json TEXT NOT NULL
);
INSERT INTO runs (
  run_id,
  state_path,
  path,
  artifact_type,
  mode,
  dry_run,
  status,
  started_at,
  completed_at,
  duration_seconds,
  reversible_operations,
  creates,
  updates,
  enables,
  disables,
  graveyard_moves,
  deletions,
  quarantined,
  conflicts,
  guardrail_failures,
  manual_review,
  unchanged,
  report_json
) VALUES (
  'run-sqlite-1',
  '${status.paths.statePath}',
  '/tmp/sqlite-run.json',
  'SyncReport',
  'Delta',
  0,
  'Succeeded',
  '2026-03-21T10:00:00Z',
  '2026-03-21T10:03:00Z',
  180,
  0,
  1,
  0,
  0,
  0,
  0,
  0,
  0,
  0,
  0,
  0,
  0,
  '${JSON.stringify(report).replaceAll("'", "''")}'
);
INSERT INTO run_entries (
  entry_id,
  run_id,
  state_path,
  bucket,
  bucket_index,
  worker_id,
  sam_account_name,
  reason,
  review_category,
  review_case_type,
  started_at,
  item_json
) VALUES (
  'run-sqlite-1:creates:2001:0',
  'run-sqlite-1',
  '${status.paths.statePath}',
  'creates',
  0,
  '2001',
  'adoe',
  NULL,
  NULL,
  NULL,
  '2026-03-21T10:00:00Z',
  '${JSON.stringify(report.creates[0]).replaceAll("'", "''")}'
);
`;
    await execFileAsync('sqlite3', [sqlitePath, sql]);

    const service = new ReportService();
    const runs = await service.listRuns(status, {});
    const detail = await service.getRun(status, 'run-sqlite-1');
    const worker = await service.getWorkerDetail(status, '2001');

    expect(runs.total).toBe(1);
    expect(runs.items[0]?.runId).toBe('run-sqlite-1');
    expect(detail.bucketCounts.creates).toBe(1);
    expect(worker.latestEntry?.entryId).toBe('run-sqlite-1:creates:2001:0');
    expect(worker.relatedEntries).toHaveLength(1);
  });

  it('throws when a configured SQLite operational store is missing', async () => {
    const status = await createStatusFixture();
    status.paths.sqlitePath = path.join(os.tmpdir(), `syncfactors-missing-${Date.now()}.db`);
    const service = new ReportService();

    await expect(service.listRuns(status, {})).rejects.toThrow(/SQLite operational store is required/);
  });

  it('prefers SQLite-backed queue queries when run entries are available', async () => {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'syncfactors-sqlite-queue-'));
    tempPaths.push(root);
    const sqlitePath = path.join(root, 'syncfactors.db');
    const status = await createStatusFixture();
    status.paths.sqlitePath = sqlitePath;
    status.paths.statePath = path.join(root, 'state.json');
    status.recentRuns = [];

    const reviewReport = {
      runId: 'run-review-1',
      artifactType: 'FirstSyncReview',
      mode: 'Review',
      dryRun: true,
      status: 'Succeeded',
      startedAt: '2026-03-22T10:00:00Z',
      completedAt: '2026-03-22T10:05:00Z',
      operations: [],
      creates: [],
      updates: [],
      enables: [],
      disables: [],
      graveyardMoves: [],
      deletions: [],
      quarantined: [],
      conflicts: [],
      guardrailFailures: [],
      manualReview: [{ workerId: '3001', reason: 'RehireDetected', reviewCaseType: 'RehireCase' }],
      unchanged: [],
    };

    const sql = `
CREATE TABLE IF NOT EXISTS runs (
  run_id TEXT PRIMARY KEY,
  state_path TEXT NULL,
  path TEXT NULL,
  artifact_type TEXT NOT NULL,
  worker_scope_json TEXT NULL,
  config_path TEXT NULL,
  mapping_config_path TEXT NULL,
  mode TEXT NULL,
  dry_run INTEGER NOT NULL DEFAULT 0,
  status TEXT NULL,
  started_at TEXT NULL,
  completed_at TEXT NULL,
  duration_seconds INTEGER NULL,
  reversible_operations INTEGER NOT NULL DEFAULT 0,
  creates INTEGER NOT NULL DEFAULT 0,
  updates INTEGER NOT NULL DEFAULT 0,
  enables INTEGER NOT NULL DEFAULT 0,
  disables INTEGER NOT NULL DEFAULT 0,
  graveyard_moves INTEGER NOT NULL DEFAULT 0,
  deletions INTEGER NOT NULL DEFAULT 0,
  quarantined INTEGER NOT NULL DEFAULT 0,
  conflicts INTEGER NOT NULL DEFAULT 0,
  guardrail_failures INTEGER NOT NULL DEFAULT 0,
  manual_review INTEGER NOT NULL DEFAULT 0,
  unchanged INTEGER NOT NULL DEFAULT 0,
  review_summary_json TEXT NULL,
  report_json TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS run_entries (
  entry_id TEXT PRIMARY KEY,
  run_id TEXT NOT NULL,
  state_path TEXT NULL,
  bucket TEXT NOT NULL,
  bucket_index INTEGER NOT NULL,
  worker_id TEXT NULL,
  sam_account_name TEXT NULL,
  reason TEXT NULL,
  review_category TEXT NULL,
  review_case_type TEXT NULL,
  started_at TEXT NULL,
  item_json TEXT NOT NULL
);
INSERT INTO runs (run_id, state_path, path, artifact_type, mode, dry_run, status, started_at, completed_at, duration_seconds, manual_review, report_json)
VALUES ('run-review-1', '${status.paths.statePath}', '/tmp/review.json', 'FirstSyncReview', 'Review', 1, 'Succeeded', '2026-03-22T10:00:00Z', '2026-03-22T10:05:00Z', 300, 1, '${JSON.stringify(reviewReport).replaceAll("'", "''")}');
INSERT INTO run_entries (entry_id, run_id, state_path, bucket, bucket_index, worker_id, reason, review_case_type, started_at, item_json)
VALUES ('run-review-1:manualReview:3001:0', 'run-review-1', '${status.paths.statePath}', 'manualReview', 0, '3001', 'RehireDetected', 'RehireCase', '2026-03-22T10:00:00Z', '${JSON.stringify(reviewReport.manualReview[0]).replaceAll("'", "''")}');
`;
    await execFileAsync('sqlite3', [sqlitePath, sql]);

    const service = new ReportService();
    const queue = await service.getQueue(status, 'manual-review', {});

    expect(queue.total).toBe(1);
    expect(queue.entries[0]?.workerId).toBe('3001');
    expect(queue.reasonGroups[0]?.label).toBe('RehireDetected');
  });

  it('prefers SQLite-backed run detail and run entry queries when entry rows are available', async () => {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'syncfactors-sqlite-run-'));
    tempPaths.push(root);
    const sqlitePath = path.join(root, 'syncfactors.db');
    const status = await createStatusFixture();
    status.paths.sqlitePath = sqlitePath;
    status.paths.statePath = path.join(root, 'state.json');
    status.recentRuns = [];

    const report = {
      runId: 'run-detail-1',
      artifactType: 'WorkerPreview',
      mode: 'Review',
      dryRun: true,
      status: 'Succeeded',
      startedAt: '2026-03-22T11:00:00Z',
      completedAt: '2026-03-22T11:04:00Z',
      operations: [
        {
          workerId: '4001',
          bucket: 'updates',
          operationType: 'UpdateAttributes',
          before: { department: 'Finance' },
          after: { department: 'Sales' },
          target: { samAccountName: 'asmith' },
        },
      ],
      creates: [],
      updates: [
        {
          workerId: '4001',
          samAccountName: 'asmith',
          reason: 'AttributeDelta',
          changedAttributeDetails: [
            {
              sourceField: 'department',
              targetAttribute: 'department',
              currentAdValue: 'Finance',
              proposedValue: 'Sales',
            },
          ],
        },
      ],
      enables: [],
      disables: [],
      graveyardMoves: [],
      deletions: [],
      quarantined: [],
      conflicts: [],
      guardrailFailures: [],
      manualReview: [],
      unchanged: [],
    };

    const sql = `
CREATE TABLE IF NOT EXISTS runs (
  run_id TEXT PRIMARY KEY,
  state_path TEXT NULL,
  path TEXT NULL,
  artifact_type TEXT NOT NULL,
  worker_scope_json TEXT NULL,
  config_path TEXT NULL,
  mapping_config_path TEXT NULL,
  mode TEXT NULL,
  dry_run INTEGER NOT NULL DEFAULT 0,
  status TEXT NULL,
  started_at TEXT NULL,
  completed_at TEXT NULL,
  duration_seconds INTEGER NULL,
  reversible_operations INTEGER NOT NULL DEFAULT 0,
  creates INTEGER NOT NULL DEFAULT 0,
  updates INTEGER NOT NULL DEFAULT 0,
  enables INTEGER NOT NULL DEFAULT 0,
  disables INTEGER NOT NULL DEFAULT 0,
  graveyard_moves INTEGER NOT NULL DEFAULT 0,
  deletions INTEGER NOT NULL DEFAULT 0,
  quarantined INTEGER NOT NULL DEFAULT 0,
  conflicts INTEGER NOT NULL DEFAULT 0,
  guardrail_failures INTEGER NOT NULL DEFAULT 0,
  manual_review INTEGER NOT NULL DEFAULT 0,
  unchanged INTEGER NOT NULL DEFAULT 0,
  review_summary_json TEXT NULL,
  report_json TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS run_entries (
  entry_id TEXT PRIMARY KEY,
  run_id TEXT NOT NULL,
  state_path TEXT NULL,
  bucket TEXT NOT NULL,
  bucket_index INTEGER NOT NULL,
  worker_id TEXT NULL,
  sam_account_name TEXT NULL,
  reason TEXT NULL,
  review_category TEXT NULL,
  review_case_type TEXT NULL,
  started_at TEXT NULL,
  item_json TEXT NOT NULL
);
INSERT INTO runs (run_id, state_path, path, artifact_type, mode, dry_run, status, started_at, completed_at, duration_seconds, reversible_operations, updates, report_json)
VALUES ('run-detail-1', '${status.paths.statePath}', '/tmp/run-detail.json', 'WorkerPreview', 'Review', 1, 'Succeeded', '2026-03-22T11:00:00Z', '2026-03-22T11:04:00Z', 240, 1, 1, '${JSON.stringify(report).replaceAll("'", "''")}');
INSERT INTO run_entries (entry_id, run_id, state_path, bucket, bucket_index, worker_id, sam_account_name, reason, started_at, item_json)
VALUES ('run-detail-1:updates:4001:0', 'run-detail-1', '${status.paths.statePath}', 'updates', 0, '4001', 'asmith', 'AttributeDelta', '2026-03-22T11:00:00Z', '${JSON.stringify(report.updates[0]).replaceAll("'", "''")}');
`;
    await execFileAsync('sqlite3', [sqlitePath, sql]);

    const service = new ReportService();
    const detail = await service.getRun(status, 'run-detail-1');
    const entries = await service.getRunEntries(status, 'run-detail-1', { bucket: 'updates', workerId: '4001' });

    expect(detail.run.runId).toBe('run-detail-1');
    expect(detail.bucketCounts.updates).toBe(1);
    expect(detail.reviewExplorer.changed).toBe(1);
    expect(entries.total).toBe(1);
    expect(entries.entries[0]?.diffRows[0]?.attribute).toBe('department');
    expect(entries.entries[0]?.operationSummary?.action).toMatch(/Update attributes/);
  });
});
