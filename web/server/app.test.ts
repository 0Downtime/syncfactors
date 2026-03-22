import request from 'supertest';
import { describe, expect, it, vi } from 'vitest';
import { createApp, createMockStatusProvider } from './app.js';
import { ReportService } from './report-service.js';

const dashboardStatus = {
  configPath: '/tmp/config.json',
  latestRun: {
    runId: 'run-1',
    path: '/tmp/run-1.json',
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
    quarantined: 1,
    conflicts: 0,
    guardrailFailures: 0,
    manualReview: 1,
    unchanged: 0,
  },
  currentRun: {
    status: 'Idle',
    stage: 'Completed',
    processedWorkers: 0,
    totalWorkers: 0,
    currentWorkerId: null,
    lastAction: 'No active sync run.',
  },
  recentRuns: [],
  summary: {
    lastCheckpoint: '2026-03-20T10:00:00Z',
    totalTrackedWorkers: 3,
    suppressedWorkers: 1,
    pendingDeletionWorkers: 0,
  },
  health: {
    successFactors: { status: 'OK', detail: 'oauth' },
    activeDirectory: { status: 'OK', detail: 'dc01' },
  },
  trackedWorkers: [],
  context: {},
  paths: {
    configPath: '/tmp/config.json',
    statePath: '/tmp/state.json',
    reportDirectory: '/tmp/reports',
    reviewReportDirectory: '/tmp/review',
    reportDirectories: ['/tmp/reports', '/tmp/review'],
    runtimeStatusPath: '/tmp/runtime-status.json',
  },
  warnings: [],
};

describe('web api', () => {
  it('returns dashboard status and new queue/worker endpoints', async () => {
    const reportService = {
      listRuns: vi.fn(async () => ({ items: [], total: 0, page: 1, pageSize: 25, warnings: [] })),
      getRun: vi.fn(async () => ({ run: dashboardStatus.latestRun, report: {}, bucketCounts: {}, warnings: [], reviewExplorer: { created: 0, changed: 0, deleted: 0 } })),
      getRunEntries: vi.fn(async () => ({ run: dashboardStatus.latestRun, entries: [], total: 0, warnings: [] })),
      getQueue: vi.fn(async () => ({ queueName: 'manual-review', entries: [], total: 0, page: 1, pageSize: 25, reasonGroups: [], reviewCaseGroups: [], artifactGroups: [], warnings: [] })),
      getWorkerHistory: vi.fn(async () => ({ workerId: '1001', entries: [], warnings: [] })),
      getWorkerDetail: vi.fn(async () => ({ workerId: '1001', trackedWorker: null, latestEntry: null, relatedEntries: [], relatedRuns: [], warnings: [] })),
    } as unknown as ReportService;

    const app = createApp({
      configPath: '/tmp/config.json',
      statusProvider: createMockStatusProvider({
        ...dashboardStatus,
        recentRuns: [dashboardStatus.latestRun],
      }),
      reportService,
    });

    const statusResponse = await request(app).get('/api/status');
    const queueResponse = await request(app).get('/api/queues/manual-review');
    const workerResponse = await request(app).get('/api/workers/1001');

    expect(statusResponse.status).toBe(200);
    expect(statusResponse.body.status.latestRun.runId).toBe('run-1');
    expect(queueResponse.status).toBe(200);
    expect(workerResponse.status).toBe(200);
  });
});
