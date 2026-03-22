import type { DashboardStatus, EntryRecord, QueueName, WorkerPreviewMode } from './types.js';

export type ViewName = 'dashboard' | 'queues' | 'worker' | 'report' | 'worker-preview' | 'operations';
export type ReportCategory = 'Changed' | 'Created' | 'Deleted';

export type RouteState = {
  view: ViewName;
  runId: string | null;
  bucket: string;
  entryId: string | null;
  filter: string;
  queueName: QueueName;
  reason: string;
  reviewCaseType: string;
  workerId: string | null;
  diffMode: 'changed' | 'all';
  reviewExplorer: 'all' | 'changed' | 'created' | 'deleted';
  page: number;
  pageSize: number;
  previewMode: WorkerPreviewMode;
  reportCategory: ReportCategory;
};

export const DEFAULT_ROUTE: RouteState = {
  view: 'dashboard',
  runId: null,
  bucket: 'quarantined',
  entryId: null,
  filter: '',
  queueName: 'manual-review',
  reason: '',
  reviewCaseType: '',
  workerId: null,
  diffMode: 'changed',
  reviewExplorer: 'all',
  page: 1,
  pageSize: 25,
  previewMode: 'full',
  reportCategory: 'Changed',
};

export const BUCKET_ORDER = [
  'quarantined',
  'conflicts',
  'manualReview',
  'guardrailFailures',
  'creates',
  'updates',
  'enables',
  'disables',
  'graveyardMoves',
  'deletions',
  'unchanged',
];

const ALLOWED_PAGE_SIZES = new Set([10, 25, 50]);

export function getRouteState(): RouteState {
  const params = new URLSearchParams(window.location.search);
  return normalizeRoute({
    view: parseView(params.get('view')),
    runId: params.get('run'),
    bucket: params.get('bucket') ?? DEFAULT_ROUTE.bucket,
    entryId: params.get('entry'),
    filter: params.get('filter') ?? '',
    queueName: parseQueueName(params.get('queue')),
    reason: params.get('reason') ?? '',
    reviewCaseType: params.get('reviewCaseType') ?? '',
    workerId: params.get('workerId') ?? params.get('worker'),
    diffMode: params.get('diff') === 'all' ? 'all' : 'changed',
    reviewExplorer: parseReviewExplorer(params.get('reviewExplorer')),
    page: parsePositiveInt(params.get('page')) ?? DEFAULT_ROUTE.page,
    pageSize: parsePageSize(params.get('pageSize')),
    previewMode: parsePreviewMode(params.get('previewMode')),
    reportCategory: parseReportCategory(params.get('reportCategory')),
  });
}

export function syncRouteState(route: RouteState, push = false) {
  const params = new URLSearchParams();
  params.set('view', route.view);
  if (route.runId) params.set('run', route.runId);
  if (route.bucket) params.set('bucket', route.bucket);
  if (route.entryId) params.set('entry', route.entryId);
  if (route.filter) params.set('filter', route.filter);
  params.set('queue', route.queueName);
  if (route.reason) params.set('reason', route.reason);
  if (route.reviewCaseType) params.set('reviewCaseType', route.reviewCaseType);
  if (route.workerId) params.set('workerId', route.workerId);
  if (route.diffMode !== 'changed') params.set('diff', route.diffMode);
  if (route.reviewExplorer !== DEFAULT_ROUTE.reviewExplorer) params.set('reviewExplorer', route.reviewExplorer);
  if (route.page !== DEFAULT_ROUTE.page) params.set('page', String(route.page));
  if (route.pageSize !== DEFAULT_ROUTE.pageSize) params.set('pageSize', String(route.pageSize));
  if (route.previewMode !== DEFAULT_ROUTE.previewMode) params.set('previewMode', route.previewMode);
  if (route.reportCategory !== DEFAULT_ROUTE.reportCategory) params.set('reportCategory', route.reportCategory);
  const url = `${window.location.pathname}?${params.toString()}`;
  if (push) {
    window.history.pushState(null, '', url);
  } else {
    window.history.replaceState(null, '', url);
  }
}

export function normalizeRoute(route: Partial<RouteState>, status?: DashboardStatus | null): RouteState {
  const next = { ...DEFAULT_ROUTE, ...route };
  if (!next.runId && status?.recentRuns?.[0]?.runId) {
    next.runId = status.recentRuns[0].runId;
  }
  if (next.reviewExplorer === 'created' || next.reviewExplorer === 'deleted') {
    next.bucket = mapReviewExplorerToBucket(next.reviewExplorer);
  }
  next.page = Math.max(Number.isFinite(next.page) ? next.page : DEFAULT_ROUTE.page, 1);
  next.pageSize = ALLOWED_PAGE_SIZES.has(next.pageSize) ? next.pageSize : DEFAULT_ROUTE.pageSize;
  return next;
}

export function chooseSelectedEntry(entries: EntryRecord[], entryId: string | null): EntryRecord | null {
  if (entries.length === 0) {
    return null;
  }
  return entries.find((entry) => entry.entryId === entryId) ?? entries[0] ?? null;
}

export function stepSelection(entries: EntryRecord[], currentEntryId: string | null, direction: 1 | -1): EntryRecord | null {
  const currentIndex = Math.max(entries.findIndex((entry) => entry.entryId === currentEntryId), 0);
  const nextIndex = Math.min(Math.max(currentIndex + direction, 0), entries.length - 1);
  return entries[nextIndex] ?? null;
}

export function resolveActiveBucket(bucket: string, reviewExplorer: 'all' | 'changed' | 'created' | 'deleted'): string | undefined {
  if (reviewExplorer === 'all') {
    return undefined;
  }
  if (reviewExplorer === 'created') {
    return 'creates';
  }
  if (reviewExplorer === 'deleted') {
    return 'deletions';
  }
  return bucket;
}

export function mapReviewExplorerToBucket(mode: 'all' | 'changed' | 'created' | 'deleted'): string {
  if (mode === 'all') {
    return 'updates';
  }
  if (mode === 'created') {
    return 'creates';
  }
  if (mode === 'deleted') {
    return 'deletions';
  }
  return 'updates';
}

export function mapEntryToReportCategory(entry: Pick<EntryRecord, 'bucket' | 'reviewCategory'>): ReportCategory {
  if (entry.bucket === 'creates' || entry.reviewCategory === 'NewUser') {
    return 'Created';
  }
  if (['updates', 'enables', 'disables', 'graveyardMoves', 'unchanged'].includes(entry.bucket)) {
    return 'Changed';
  }
  return 'Deleted';
}

function parseView(value: string | null): ViewName {
  if (value === 'queues' || value === 'worker' || value === 'report' || value === 'worker-preview' || value === 'operations') {
    return value;
  }
  return 'dashboard';
}

function parsePreviewMode(value: string | null): WorkerPreviewMode {
  return value === 'minimal' ? 'minimal' : 'full';
}

function parseReportCategory(value: string | null): ReportCategory {
  if (value === 'Created' || value === 'Deleted') {
    return value;
  }
  return 'Changed';
}

function parseQueueName(value: string | null): QueueName {
  if (value === 'quarantined' || value === 'conflicts' || value === 'guardrails') {
    return value;
  }
  return 'manual-review';
}

function parseReviewExplorer(value: string | null): 'all' | 'changed' | 'created' | 'deleted' {
  if (value === 'all' || value === 'changed' || value === 'created' || value === 'deleted') {
    return value;
  }
  return DEFAULT_ROUTE.reviewExplorer;
}

function parsePositiveInt(value: string | null): number | null {
  if (!value) {
    return null;
  }
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

function parsePageSize(value: string | null): number {
  const parsed = parsePositiveInt(value);
  return parsed && ALLOWED_PAGE_SIZES.has(parsed) ? parsed : DEFAULT_ROUTE.pageSize;
}
