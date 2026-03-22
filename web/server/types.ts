export type HealthStatus = {
  status: string;
  detail: string;
};

export type RunSummary = {
  runId: string | null;
  path: string | null;
  artifactType: string;
  workerScope?: { workerId?: string | null; identityField?: string | null } | null;
  configPath?: string | null;
  mappingConfigPath?: string | null;
  mode: string | null;
  dryRun: boolean;
  status: string | null;
  startedAt: string | null;
  completedAt: string | null;
  durationSeconds: number | null;
  reversibleOperations: number;
  creates: number;
  updates: number;
  enables: number;
  disables: number;
  graveyardMoves: number;
  deletions: number;
  quarantined: number;
  conflicts: number;
  guardrailFailures: number;
  manualReview: number;
  unchanged: number;
  reviewSummary?: Record<string, unknown> | null;
};

export type DashboardStatus = {
  configPath: string;
  latestRun: RunSummary;
  currentRun: Record<string, unknown>;
  recentRuns: RunSummary[];
  summary: {
    lastCheckpoint: string | null;
    totalTrackedWorkers: number;
    suppressedWorkers: number;
    pendingDeletionWorkers: number;
  };
  health: {
    successFactors: HealthStatus;
    activeDirectory: HealthStatus;
  };
  trackedWorkers: TrackedWorker[];
  context: Record<string, unknown>;
  paths: {
    configPath: string;
    statePath: string;
    reportDirectory: string;
    reviewReportDirectory: string;
    reportDirectories: string[];
    runtimeStatusPath: string;
    sqlitePath?: string | null;
  };
  warnings?: string[];
};

export type TrackedWorker = {
  workerId: string;
  adObjectGuid?: string | null;
  distinguishedName?: string | null;
  suppressed?: boolean;
  firstDisabledAt?: string | null;
  deleteAfter?: string | null;
  lastSeenStatus?: string | null;
};

export type OperatorAction = {
  code?: string;
  label?: string;
  description?: string;
};

export type DiffRow = {
  attribute: string;
  source: string | null;
  before: string;
  after: string;
  changed: boolean;
};

export type OperationSummary = {
  action: string;
  effect: string | null;
  targetOu: string | null;
  fromOu: string | null;
  toOu: string | null;
};

export type EntryRecord = {
  entryId: string;
  runId: string | null;
  reportPath: string | null;
  artifactType: string;
  mode: string | null;
  bucket: string;
  bucketLabel: string;
  queueName: QueueName | null;
  workerId: string | null;
  samAccountName: string | null;
  reason: string | null;
  reviewCategory: string | null;
  reviewCaseType: string | null;
  groupKey: string;
  groupLabel: string;
  operatorActionSummary: string | null;
  operatorActions: OperatorAction[];
  targetOu: string | null;
  currentDistinguishedName: string | null;
  currentEnabled: boolean | null;
  proposedEnable: boolean | null;
  matchedExistingUser: boolean | null;
  changeCount: number;
  startedAt: string | null;
  staleDays: number | null;
  operationSummary: OperationSummary | null;
  diffRows: DiffRow[];
  item: Record<string, unknown>;
};

export type RunDetailResponse = {
  run: RunSummary;
  report: Record<string, unknown>;
  bucketCounts: Record<string, number>;
  warnings: string[];
  reviewExplorer: {
    created: number;
    changed: number;
    deleted: number;
  };
};

export type EntryListResponse = {
  run: RunSummary;
  entries: EntryRecord[];
  total: number;
  warnings: string[];
};

export type QueueName = 'manual-review' | 'quarantined' | 'conflicts' | 'guardrails';

export type QueueGroup = {
  key: string;
  label: string;
  count: number;
};

export type QueueResponse = {
  queueName: QueueName;
  entries: EntryRecord[];
  total: number;
  page: number;
  pageSize: number;
  reasonGroups: QueueGroup[];
  reviewCaseGroups: QueueGroup[];
  artifactGroups: QueueGroup[];
  warnings: string[];
};

export type WorkerHistoryResponse = {
  workerId: string;
  entries: EntryRecord[];
  warnings: string[];
};

export type WorkerDetailResponse = {
  workerId: string;
  trackedWorker: TrackedWorker | null;
  latestEntry: EntryRecord | null;
  relatedEntries: EntryRecord[];
  relatedRuns: RunSummary[];
  warnings: string[];
};

export type WorkerActionKind = 'test-sync' | 'review-sync' | 'real-sync';

export type WorkerActionResponse = {
  action: WorkerActionKind;
  workerId: string;
  result: {
    reportPath: string | null;
    runId: string | null;
    mode: string | null;
    status: string | null;
    artifactType: string | null;
    previewMode?: string | null;
    successFactorsAuth?: string | null;
    workerScope?: { workerId?: string | null; identityField?: string | null } | null;
  };
};
