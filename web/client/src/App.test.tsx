// @vitest-environment jsdom
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, expect, vi } from 'vitest';
import { App } from './App.js';
import { DEFAULT_ROUTE } from './route-state.js';
import { getToneForReviewEntry } from './triage-components.js';
import { DashboardView } from './triage-views.js';

const NON_WINDOWS_AD_WARNING = 'Active Directory health probe is skipped on non-Windows hosts for the web dashboard.';

const mockGetStatus = vi.fn();
const mockGetRun = vi.fn();
const mockGetRunEntries = vi.fn();
const mockGetQueue = vi.fn();
const mockGetWorkerDetail = vi.fn();

vi.mock('./api.js', () => ({
  getStatus: (...args: unknown[]) => mockGetStatus(...args),
  getRun: (...args: unknown[]) => mockGetRun(...args),
  getRunEntries: (...args: unknown[]) => mockGetRunEntries(...args),
  getQueue: (...args: unknown[]) => mockGetQueue(...args),
  getWorkerHistory: vi.fn(async () => ({ workerId: '1001', entries: [], warnings: [] })),
  getWorkerDetail: (...args: unknown[]) => mockGetWorkerDetail(...args),
}));

beforeEach(() => {
  window.history.replaceState(null, '', '/');
  mockGetStatus.mockReset();
  mockGetRun.mockReset();
  mockGetRunEntries.mockReset();
  mockGetQueue.mockReset();
  mockGetWorkerDetail.mockReset();

  mockGetStatus.mockResolvedValue({
    latestRun: {
      runId: 'run-1',
      path: '/tmp/run-1.json',
      artifactType: 'WorkerPreview',
      mode: 'Review',
      dryRun: true,
      status: 'Succeeded',
      startedAt: '2026-03-20T10:00:00Z',
      durationSeconds: 300,
      creates: 0,
      updates: 1,
      enables: 0,
      disables: 0,
      deletions: 0,
      quarantined: 0,
      conflicts: 0,
      guardrailFailures: 0,
      manualReview: 1,
      unchanged: 0,
      graveyardMoves: 0,
    },
    currentRun: {
      status: 'InProgress',
      stage: 'ProcessingWorkers',
      processedWorkers: 2,
      totalWorkers: 10,
      currentWorkerId: '1001',
      lastAction: 'Evaluating worker 1001.',
    },
    recentRuns: [
      {
        runId: 'run-1',
        path: '/tmp/run-1.json',
        artifactType: 'WorkerPreview',
        mode: 'Review',
        dryRun: true,
        status: 'Succeeded',
        startedAt: '2026-03-20T10:00:00Z',
        durationSeconds: 300,
        creates: 0,
        updates: 1,
        enables: 0,
        disables: 0,
        deletions: 0,
        quarantined: 0,
        conflicts: 0,
        guardrailFailures: 0,
        manualReview: 1,
        unchanged: 0,
        graveyardMoves: 0,
        workerScope: { workerId: '1001' },
      },
    ],
    summary: {
      lastCheckpoint: '2026-03-20T09:00:00Z',
      totalTrackedWorkers: 7,
      suppressedWorkers: 1,
      pendingDeletionWorkers: 0,
    },
    health: {
      successFactors: { status: 'OK', detail: 'oauth' },
      activeDirectory: { status: 'OK', detail: 'dc01' },
    },
    trackedWorkers: [{ workerId: '1001', suppressed: true, distinguishedName: 'CN=Jamie Doe' }],
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
  });

  mockGetRun.mockResolvedValue({
    run: {
      runId: 'run-1',
      path: '/tmp/run-1.json',
      artifactType: 'WorkerPreview',
      mode: 'Review',
      dryRun: true,
      status: 'Succeeded',
      startedAt: '2026-03-20T10:00:00Z',
      completedAt: '2026-03-20T11:05:00Z',
      durationSeconds: 300,
      creates: 0,
      updates: 1,
      enables: 0,
      disables: 0,
      deletions: 0,
      quarantined: 0,
      conflicts: 0,
      guardrailFailures: 0,
      manualReview: 1,
      unchanged: 0,
      graveyardMoves: 0,
    },
    report: {},
    bucketCounts: {
      updates: 1,
      manualReview: 1,
      creates: 1,
      deletions: 1,
      quarantined: 0,
      conflicts: 0,
      guardrailFailures: 0,
      enables: 0,
      disables: 0,
      graveyardMoves: 0,
      unchanged: 0,
    },
    warnings: ['Skipped malformed report'],
    reviewExplorer: { created: 1, changed: 1, deleted: 1 },
  });

  mockGetRunEntries.mockImplementation(async (_runId: string, query?: { bucket?: string }) => {
    const entries = [
      {
        entryId: 'run-1:creates:1000:0',
        bucket: 'creates',
        bucketLabel: 'Creates',
        queueName: null,
        workerId: '1000',
        samAccountName: 'newuser',
        reason: null,
        reviewCategory: 'NewUser',
        reviewCaseType: null,
        groupKey: 'newuser::workerpreview',
        groupLabel: 'NewUser',
        operatorActionSummary: 'Create account newuser',
        operatorActions: [],
        targetOu: 'OU=Employees,DC=example,DC=com',
        currentDistinguishedName: null,
        currentEnabled: null,
        proposedEnable: true,
        matchedExistingUser: false,
        changeCount: 1,
        startedAt: '2026-03-20T10:00:00Z',
        staleDays: 0,
        operationSummary: { action: 'Create account newuser', effect: 'New account.', targetOu: 'OU=Employees,DC=example,DC=com', fromOu: null, toOu: null },
        diffRows: [],
        artifactType: 'WorkerPreview',
        mode: 'Review',
        reportPath: '/tmp/run-1.json',
        runId: 'run-1',
        item: {},
      },
      {
        entryId: 'run-1:updates:1001:0',
        bucket: 'updates',
        bucketLabel: 'Updates',
        queueName: 'manual-review',
        workerId: '1001',
        samAccountName: 'jdoe',
        reason: 'AttributeDelta',
        reviewCategory: 'ExistingUserChanges',
        reviewCaseType: 'RehireCase',
        groupKey: 'rehirecase::workerpreview',
        groupLabel: 'RehireCase',
        operatorActionSummary: 'Confirm how this rehire should reuse or restore the existing AD identity.',
        operatorActions: [{ label: 'Confirm account reuse', description: 'Reuse the prior account.' }],
        targetOu: 'OU=Employees,DC=example,DC=com',
        currentDistinguishedName: 'CN=Jamie Doe',
        currentEnabled: true,
        proposedEnable: true,
        matchedExistingUser: true,
        changeCount: 1,
        startedAt: '2026-03-20T10:00:00Z',
        staleDays: 1,
        operationSummary: { action: 'Update attributes for jdoe', effect: '1 attribute changes.', targetOu: null, fromOu: null, toOu: null },
        diffRows: [
          { attribute: 'department', source: 'department', before: 'Finance', after: 'Sales', changed: true },
        ],
        artifactType: 'WorkerPreview',
        mode: 'Review',
        reportPath: '/tmp/run-1.json',
        runId: 'run-1',
        item: {},
      },
      {
        entryId: 'run-1:deletions:1002:0',
        bucket: 'deletions',
        bucketLabel: 'Deletions',
        queueName: null,
        workerId: '1002',
        samAccountName: 'retireduser',
        reason: 'NoLongerInSource',
        reviewCategory: null,
        reviewCaseType: null,
        groupKey: 'nolongerinsource::workerpreview',
        groupLabel: 'NoLongerInSource',
        operatorActionSummary: 'Delete account retireduser',
        operatorActions: [],
        targetOu: null,
        currentDistinguishedName: 'CN=Retired User',
        currentEnabled: false,
        proposedEnable: false,
        matchedExistingUser: true,
        changeCount: 1,
        startedAt: '2026-03-20T10:00:00Z',
        staleDays: 2,
        operationSummary: { action: 'Delete account retireduser', effect: 'Delete account.', targetOu: null, fromOu: null, toOu: null },
        diffRows: [],
        artifactType: 'WorkerPreview',
        mode: 'Review',
        reportPath: '/tmp/run-1.json',
        runId: 'run-1',
        item: {},
      },
    ];

    const filtered = query?.bucket ? entries.filter((entry) => entry.bucket === query.bucket) : entries;
    return {
      run: { runId: 'run-1' },
      total: filtered.length,
      warnings: [],
      entries: filtered,
    };
  });

  mockGetQueue.mockImplementation(async (_queueName: string, query?: { page?: number; pageSize?: number }) => {
    const page = query?.page ?? 1;
    const pageSize = query?.pageSize ?? 25;

    const pageOneEntry = {
      entryId: 'run-2:manualReview:1001:0',
      bucket: 'manualReview',
      bucketLabel: 'Manual Review',
      queueName: 'manual-review',
      workerId: '1001',
      samAccountName: 'jdoe',
      reason: 'RehireDetected',
      reviewCategory: null,
      reviewCaseType: 'RehireCase',
      groupKey: 'rehiredetected::firstsyncreview',
      groupLabel: 'RehireDetected',
      operatorActionSummary: 'Review before retry.',
      operatorActions: [],
      targetOu: null,
      currentDistinguishedName: 'CN=Jamie Doe',
      currentEnabled: false,
      proposedEnable: true,
      matchedExistingUser: true,
      changeCount: 0,
      startedAt: '2026-03-20T11:00:00Z',
      staleDays: 1,
      operationSummary: null,
      diffRows: [],
      artifactType: 'FirstSyncReview',
      mode: 'Review',
      reportPath: '/tmp/run-2.json',
      runId: 'run-2',
      item: {},
    };

    const pageTwoEntry = {
      ...pageOneEntry,
      entryId: 'run-3:manualReview:1002:0',
      workerId: '1002',
      samAccountName: 'adoe',
      reason: 'QuarantineReview',
      reviewCaseType: 'QuarantineCase',
      groupKey: 'quarantinereview::firstsyncreview',
      groupLabel: 'QuarantineReview',
      currentDistinguishedName: 'CN=Alex Doe',
      reportPath: '/tmp/run-3.json',
      runId: 'run-3',
    };

    return {
      queueName: 'manual-review',
      entries: page === 1 ? [pageOneEntry] : [pageTwoEntry],
      total: 30,
      page,
      pageSize,
      reasonGroups: [
        { key: 'RehireDetected', label: 'RehireDetected', count: 1 },
        { key: 'QuarantineReview', label: 'QuarantineReview', count: 1 },
      ],
      reviewCaseGroups: [
        { key: 'RehireCase', label: 'RehireCase', count: 1 },
        { key: 'QuarantineCase', label: 'QuarantineCase', count: 1 },
      ],
      artifactGroups: [{ key: 'FirstSyncReview', label: 'FirstSyncReview', count: 2 }],
      warnings: [],
    };
  });

  mockGetWorkerDetail.mockResolvedValue({
    workerId: '1001',
    trackedWorker: {
      workerId: '1001',
      suppressed: true,
      distinguishedName: 'CN=Jamie Doe',
      deleteAfter: '2026-03-30T10:00:00Z',
      lastSeenStatus: 'active',
    },
    latestEntry: {
      entryId: 'run-1:updates:1001:0',
      bucket: 'Updates',
      bucketLabel: 'Updates',
      queueName: 'manual-review',
      workerId: '1001',
      samAccountName: 'jdoe',
      reason: 'AttributeDelta',
      reviewCategory: 'ExistingUserChanges',
      reviewCaseType: 'RehireCase',
      groupKey: 'rehirecase::workerpreview',
      groupLabel: 'RehireCase',
      operatorActionSummary: 'Review before retry.',
      operatorActions: [],
      targetOu: null,
      currentDistinguishedName: 'CN=Jamie Doe',
      currentEnabled: true,
      proposedEnable: true,
      matchedExistingUser: true,
      changeCount: 1,
      startedAt: '2026-03-20T10:00:00Z',
      staleDays: 1,
      operationSummary: { action: 'Update attributes for jdoe', effect: '1 attribute changes.', targetOu: null, fromOu: null, toOu: null },
      diffRows: [{ attribute: 'department', source: 'department', before: 'Finance', after: 'Sales', changed: true }],
      artifactType: 'WorkerPreview',
      mode: 'Review',
      reportPath: '/tmp/run-1.json',
      runId: 'run-1',
      item: {},
    },
    relatedEntries: [
      {
        entryId: 'run-2:manualReview:1001:0',
        bucket: 'manualReview',
        bucketLabel: 'Manual Review',
        queueName: 'manual-review',
        workerId: '1001',
        samAccountName: 'jdoe',
        reason: 'RehireDetected',
        reviewCategory: null,
        reviewCaseType: 'RehireCase',
        groupKey: 'rehiredetected::firstsyncreview',
        groupLabel: 'RehireDetected',
        operatorActionSummary: null,
        operatorActions: [],
        targetOu: null,
        currentDistinguishedName: 'CN=Jamie Doe',
        currentEnabled: false,
        proposedEnable: true,
        matchedExistingUser: true,
        changeCount: 0,
        startedAt: '2026-03-20T11:00:00Z',
        staleDays: 1,
        operationSummary: null,
        diffRows: [],
        artifactType: 'FirstSyncReview',
        mode: 'Review',
        reportPath: '/tmp/run-2.json',
        runId: 'run-2',
        item: {},
      },
    ],
    relatedRuns: [
      {
        runId: 'run-2',
        path: '/tmp/run-2.json',
        artifactType: 'FirstSyncReview',
        mode: 'Review',
        dryRun: true,
        status: 'Succeeded',
        startedAt: '2026-03-20T11:00:00Z',
        durationSeconds: 180,
        creates: 0,
        updates: 0,
        enables: 0,
        disables: 0,
        deletions: 0,
        quarantined: 0,
        conflicts: 0,
        guardrailFailures: 0,
        manualReview: 1,
        unchanged: 0,
        graveyardMoves: 0,
      },
    ],
    warnings: [],
  });
});

describe('App', () => {
  it('shows absolute and live relative timestamps in run detail', async () => {
    vi.useFakeTimers();
    try {
      vi.setSystemTime(new Date('2026-03-20T12:05:00Z'));
      const runDetail = await mockGetRun();

      render(
        <DashboardView
          status={null}
          route={{ ...DEFAULT_ROUTE, runId: 'run-1' }}
          runDetail={runDetail}
          entryResponse={{ run: runDetail.run, entries: [], total: 0, warnings: [] }}
          selectedEntry={null}
          runBuckets={[]}
          filterInputRef={{ current: null }}
          onSelectRun={() => undefined}
          onSelectBucket={() => undefined}
          onFilterChange={() => undefined}
          onSelectEntry={() => undefined}
          onChangeDiffMode={() => undefined}
          onChangeReviewExplorer={() => undefined}
          onOpenWorker={() => undefined}
        />,
      );

      expect(screen.getByLabelText(/Run timeline/i)).toBeInTheDocument();
      expect(screen.getAllByText(/Mar 20, 2026/i).length).toBeGreaterThanOrEqual(2);
      expect(screen.getByText('2 hours ago')).toBeInTheDocument();
      expect(screen.getByText('1 hour ago')).toBeInTheDocument();

      vi.setSystemTime(new Date('2026-03-20T13:05:00Z'));
      await act(async () => {
        vi.advanceTimersByTime(30000);
      });

      expect(screen.getByText('3 hours ago')).toBeInTheDocument();
      expect(screen.getByText('2 hours ago')).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('classifies review explorer tones by operation type instead of raw bucket', () => {
    expect(getToneForReviewEntry({ bucket: 'manualReview', reviewCategory: null })).toBe('delete');
    expect(getToneForReviewEntry({ bucket: 'quarantined', reviewCategory: null })).toBe('delete');
    expect(getToneForReviewEntry({ bucket: 'updates', reviewCategory: null })).toBe('update');
    expect(getToneForReviewEntry({ bucket: 'creates', reviewCategory: 'NewUser' })).toBe('create');
  });

  it('renders dashboard, queue, and worker triage views with url-backed state', async () => {
    render(<App />);

    await waitFor(() => expect(screen.getByText(/SyncFactors Operator UI/i)).toBeInTheDocument());
    await waitFor(() => expect(screen.getByText(/Structured diff/i)).toBeInTheDocument());
    expect(mockGetRunEntries).toHaveBeenCalledWith('run-1', expect.not.objectContaining({ bucket: expect.anything(), entryId: expect.anything() }));
    expect(screen.getByRole('button', { name: 'all (3)' })).toBeInTheDocument();
    expect(screen.getAllByText('1000').length).toBeGreaterThan(0);
    expect(screen.getAllByText('1001').length).toBeGreaterThan(0);
    expect(screen.getAllByText('1002').length).toBeGreaterThan(0);
    expect(screen.getAllByText(/ago/).length).toBeGreaterThan(0);
    expect(window.location.search).toMatch(/run=run-1/);

    fireEvent.click(screen.getByRole('button', { name: 'Queues' }));
    await waitFor(() => expect(screen.getByText(/Queue Results/i)).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /RehireDetected \(1\)/ })).toBeInTheDocument();
    expect(screen.getByText(/Showing 1-25 of 30/i)).toBeInTheDocument();
    expect(window.location.search).toMatch(/view=queues/);
    expect(mockGetQueue).toHaveBeenLastCalledWith('manual-review', expect.objectContaining({ page: 1, pageSize: 25 }));

    fireEvent.change(screen.getByLabelText(/Queue page size/i), { target: { value: '10' } });
    await waitFor(() => expect(mockGetQueue).toHaveBeenLastCalledWith('manual-review', expect.objectContaining({ page: 1, pageSize: 10 })));
    expect(window.location.search).toMatch(/pageSize=10/);

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(screen.getByText(/1002/i)).toBeInTheDocument());
    expect(window.location.search).toMatch(/page=2/);

    fireEvent.click(screen.getAllByRole('button', { name: 'Worker' })[1]);
    await waitFor(() => expect(screen.getByText(/Related Runs/i)).toBeInTheDocument());
    expect(screen.getByText(/^true$/i)).toBeInTheDocument();
    expect(screen.getAllByText(/CN=Jamie Doe/i)).toHaveLength(2);
    expect(screen.getAllByText(/ago/).length).toBeGreaterThan(0);
    expect(window.location.search).toMatch(/view=worker/);
  });

  it('shows the non-Windows AD probe warning once as a subtle status note', async () => {
    mockGetStatus.mockResolvedValueOnce({
      ...(await mockGetStatus()),
      health: {
        successFactors: { status: 'OK', detail: 'oauth' },
        activeDirectory: { status: 'UNKNOWN', detail: 'Active Directory health probe requires the Windows ActiveDirectory module.' },
      },
      warnings: [NON_WINDOWS_AD_WARNING],
    });

    render(<App />);

    await waitFor(() => expect(screen.getByText('Active Directory health is unavailable on this macOS host.')).toBeInTheDocument());
    expect(screen.queryByText(/Status warnings/i)).not.toBeInTheDocument();
    expect(screen.getByText('Active Directory health is unavailable on this macOS host.')).toBeInTheDocument();
    expect(screen.queryByText(NON_WINDOWS_AD_WARNING)).not.toBeInTheDocument();
  });
});
