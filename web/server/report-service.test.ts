import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { ReportService } from './report-service.js';
import type { DashboardStatus } from './types.js';

const tempPaths: string[] = [];
const NON_WINDOWS_AD_PROBE_WARNING = 'Active Directory health probe is skipped on non-Windows hosts for the web dashboard.';

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
});
