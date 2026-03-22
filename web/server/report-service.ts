import fs from 'node:fs/promises';
import path from 'node:path';
import type {
  DashboardStatus,
  DiffRow,
  EntryListResponse,
  EntryRecord,
  OperationSummary,
  QueueGroup,
  QueueName,
  QueueResponse,
  RunDetailResponse,
  RunSummary,
  TrackedWorker,
  WorkerDetailResponse,
  WorkerHistoryResponse,
} from './types.js';
import { SqliteStore, type SqliteEntryRecord } from './sqlite-store.js';

const BUCKET_LABELS: Record<string, string> = {
  creates: 'Creates',
  updates: 'Updates',
  enables: 'Enables',
  disables: 'Disables',
  graveyardMoves: 'Graveyard Moves',
  deletions: 'Deletions',
  quarantined: 'Quarantined',
  conflicts: 'Conflicts',
  guardrailFailures: 'Guardrails',
  manualReview: 'Manual Review',
  unchanged: 'Unchanged',
};

const BUCKET_ORDER = [
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
] as const;

const QUEUE_BUCKETS: Record<QueueName, string[]> = {
  'manual-review': ['manualReview'],
  quarantined: ['quarantined'],
  conflicts: ['conflicts'],
  guardrails: ['guardrailFailures'],
};

type ReportCacheEntry = {
  mtimeMs: number;
  report: Record<string, unknown>;
};

type ScannedRun = {
  run: RunSummary;
  report: Record<string, unknown>;
};

type ScanResult = {
  runs: ScannedRun[];
  warnings: string[];
};

const NON_WINDOWS_AD_PROBE_WARNING = 'Active Directory health probe is skipped on non-Windows hosts for the web dashboard.';

export class ReportService {
  private readonly reportCache = new Map<string, ReportCacheEntry>();
  private readonly sqliteStore = new SqliteStore();

  async listRuns(
    status: DashboardStatus,
    filters: { mode?: string; artifact?: string; status?: string; page?: number; pageSize?: number },
  ): Promise<{ items: RunSummary[]; total: number; page: number; pageSize: number; warnings: string[] }> {
    const page = Math.max(filters.page ?? 1, 1);
    const pageSize = Math.min(Math.max(filters.pageSize ?? 25, 1), 100);
    const scan = await this.scanRuns(status);
    const filtered = scan.runs
      .map(({ run }) => run)
      .filter((run) => {
        if (filters.mode && `${run.mode ?? ''}`.toLowerCase() !== filters.mode.toLowerCase()) {
          return false;
        }
        if (filters.artifact && `${run.artifactType}`.toLowerCase() !== filters.artifact.toLowerCase()) {
          return false;
        }
        if (filters.status && `${run.status ?? ''}`.toLowerCase() !== filters.status.toLowerCase()) {
          return false;
        }
        return true;
      });

    const start = (page - 1) * pageSize;
    return {
      items: filtered.slice(start, start + pageSize),
      total: filtered.length,
      page,
      pageSize,
      warnings: scan.warnings,
    };
  }

  async getRun(status: DashboardStatus, runId: string): Promise<RunDetailResponse> {
    const sqlitePath = status.paths.sqlitePath;
    if (sqlitePath) {
      const sqliteRun = await this.sqliteStore.getRun(sqlitePath, runId);
      if (!sqliteRun) {
        throw new Error(`Run '${runId}' was not found in the SQLite operational store.`);
      }
      const sqliteEntries = await this.sqliteStore.getRunEntries(sqlitePath, runId, {});
      const materialized = sqliteEntries.map((entry) => materializeSqliteEntry(entry, status.trackedWorkers));
      const bucketCounts = Object.fromEntries(BUCKET_ORDER.map((bucket) => [bucket, materialized.filter((entry) => entry.bucket === bucket).length]));

      return {
        run: sqliteRun.run,
        report: sqliteRun.report,
        bucketCounts,
        warnings: this.getContextWarnings(status.warnings ?? []),
        reviewExplorer: {
          created: materialized.filter((entry) => isReviewCreated(entry)).length,
          changed: materialized.filter((entry) => isReviewChanged(entry)).length,
          deleted: materialized.filter((entry) => isReviewDeleted(entry)).length,
        },
      };
    }

    const scan = await this.scanRuns(status);
    const found = this.findScannedRun(scan.runs, runId);
    const bucketCounts = Object.fromEntries(BUCKET_ORDER.map((bucket) => [bucket, arrayOf(found.report[bucket]).length]));
    const flattened = flattenEntries(found.run, found.report, status.trackedWorkers);

    return {
      run: found.run,
      report: found.report,
      bucketCounts,
      warnings: this.getContextWarnings(scan.warnings),
      reviewExplorer: {
        created: flattened.filter((entry) => isReviewCreated(entry)).length,
        changed: flattened.filter((entry) => isReviewChanged(entry)).length,
        deleted: flattened.filter((entry) => isReviewDeleted(entry)).length,
      },
    };
  }

  async getRunEntries(
    status: DashboardStatus,
    runId: string,
    filters: { bucket?: string; workerId?: string; reason?: string; filter?: string; entryId?: string },
  ): Promise<EntryListResponse> {
    const sqlitePath = status.paths.sqlitePath;
    if (sqlitePath) {
      const sqliteRun = await this.sqliteStore.getRun(sqlitePath, runId);
      if (!sqliteRun) {
        throw new Error(`Run '${runId}' was not found in the SQLite operational store.`);
      }
      const sqliteEntries = await this.sqliteStore.getRunEntries(sqlitePath, runId, filters);
      const materialized = sqliteEntries
        .map((entry) => materializeSqliteEntry(entry, status.trackedWorkers))
        .filter((entry) => matchesRunEntry(entry, filters));
      return {
        run: sqliteRun.run,
        entries: materialized,
        total: materialized.length,
        warnings: this.getContextWarnings(status.warnings ?? []),
      };
    }

    const scan = await this.scanRuns(status);
    const found = this.findScannedRun(scan.runs, runId);
    const entries = flattenEntries(found.run, found.report, status.trackedWorkers).filter((entry) => {
      if (filters.bucket && entry.bucket !== filters.bucket) {
        return false;
      }
      if (filters.workerId && entry.workerId !== filters.workerId) {
        return false;
      }
      if (filters.entryId && entry.entryId !== filters.entryId) {
        return false;
      }
      if (filters.reason && `${entry.reason ?? ''}`.toLowerCase() !== filters.reason.toLowerCase()) {
        return false;
      }
      if (filters.filter && !matchesFilter(entry, filters.filter)) {
        return false;
      }
      return true;
    });

    return { run: found.run, entries, total: entries.length, warnings: this.getContextWarnings(scan.warnings) };
  }

  async getQueue(
    status: DashboardStatus,
    queueName: QueueName,
    filters: { reason?: string; reviewCaseType?: string; workerId?: string; filter?: string; page?: number; pageSize?: number },
  ): Promise<QueueResponse> {
    const page = Math.max(filters.page ?? 1, 1);
    const pageSize = Math.min(Math.max(filters.pageSize ?? 25, 1), 100);
    const sqlitePath = status.paths.sqlitePath;
    if (sqlitePath) {
      const sqliteEntries = await this.sqliteStore.getQueueEntries(sqlitePath, status.paths.statePath, queueName, {
        reason: filters.reason,
        reviewCaseType: filters.reviewCaseType,
        workerId: filters.workerId,
        filter: filters.filter,
      });
      const materialized = sqliteEntries
        .map((entry) => materializeSqliteEntry(entry, status.trackedWorkers))
        .filter((entry) => matchesQueueEntry(entry, filters));
      const start = (page - 1) * pageSize;
      return {
        queueName,
        entries: materialized.slice(start, start + pageSize),
        total: materialized.length,
        page,
        pageSize,
        reasonGroups: buildGroups(materialized, (entry) => entry.reason ?? 'Other'),
        reviewCaseGroups: buildGroups(materialized, (entry) => entry.reviewCaseType ?? 'Other'),
        artifactGroups: buildGroups(materialized, (entry) => entry.artifactType),
        warnings: this.getContextWarnings(status.warnings ?? []),
      };
    }

    const scan = await this.scanRuns(status);
    const buckets = new Set(QUEUE_BUCKETS[queueName]);
    const allEntries = scan.runs.flatMap(({ run, report }) =>
      flattenEntries(run, report, status.trackedWorkers).filter((entry) => buckets.has(entry.bucket)),
    );
    const filtered = allEntries.filter((entry) => {
      if (filters.reason && `${entry.reason ?? ''}`.toLowerCase() !== filters.reason.toLowerCase()) {
        return false;
      }
      if (filters.reviewCaseType && `${entry.reviewCaseType ?? ''}`.toLowerCase() !== filters.reviewCaseType.toLowerCase()) {
        return false;
      }
      if (filters.workerId && entry.workerId !== filters.workerId) {
        return false;
      }
      if (filters.filter && !matchesFilter(entry, filters.filter)) {
        return false;
      }
      return true;
    });

    const start = (page - 1) * pageSize;
    return {
      queueName,
      entries: filtered.slice(start, start + pageSize),
      total: filtered.length,
      page,
      pageSize,
      reasonGroups: buildGroups(filtered, (entry) => entry.reason ?? 'Other'),
      reviewCaseGroups: buildGroups(filtered, (entry) => entry.reviewCaseType ?? 'Other'),
      artifactGroups: buildGroups(filtered, (entry) => entry.artifactType),
      warnings: this.getContextWarnings(scan.warnings),
    };
  }

  async getWorkerHistory(status: DashboardStatus, workerId: string, limit = 100): Promise<WorkerHistoryResponse> {
    const detail = await this.getWorkerDetail(status, workerId, limit);
    return {
      workerId,
      entries: detail.relatedEntries,
      warnings: detail.warnings,
    };
  }

  async getWorkerDetail(status: DashboardStatus, workerId: string, limit = 100): Promise<WorkerDetailResponse> {
    const sqlitePath = status.paths.sqlitePath;
    if (sqlitePath) {
      const sqliteEntries = await this.sqliteStore.getWorkerEntries(sqlitePath, status.paths.statePath, workerId, limit);
      const relatedEntries = sqliteEntries.map((entry) => materializeSqliteEntry(entry, status.trackedWorkers)).slice(0, limit);
      const seenRuns = new Map<string, RunSummary>();
      for (const entry of sqliteEntries) {
        if (entry.run.runId && !seenRuns.has(entry.run.runId)) {
          seenRuns.set(entry.run.runId, entry.run);
        }
      }

      return {
        workerId,
        trackedWorker: status.trackedWorkers.find((worker) => worker.workerId === workerId) ?? null,
        latestEntry: relatedEntries[0] ?? null,
        relatedEntries,
        relatedRuns: [...seenRuns.values()],
        warnings: this.getContextWarnings(status.warnings ?? []),
      };
    }

    const scan = await this.scanRuns(status);
    const relatedEntries: EntryRecord[] = [];
    const relatedRuns: RunSummary[] = [];
    for (const { run, report } of scan.runs) {
      const runEntries = flattenEntries(run, report, status.trackedWorkers).filter((entry) => entry.workerId === workerId);
      if (runEntries.length > 0) {
        relatedEntries.push(...runEntries);
        relatedRuns.push(run);
      }
      if (relatedEntries.length >= limit) {
        break;
      }
    }

    return {
      workerId,
      trackedWorker: status.trackedWorkers.find((worker) => worker.workerId === workerId) ?? null,
      latestEntry: relatedEntries[0] ?? null,
      relatedEntries: relatedEntries.slice(0, limit),
      relatedRuns,
      warnings: this.getContextWarnings(scan.warnings),
    };
  }

  private getContextWarnings(warnings: string[]): string[] {
    return warnings.filter((warning) => warning !== NON_WINDOWS_AD_PROBE_WARNING);
  }

  private async scanRuns(status: DashboardStatus): Promise<ScanResult> {
    const warnings = [...(status.warnings ?? [])];
    const sqlitePath = status.paths.sqlitePath;
    if (sqlitePath) {
      const sqliteRuns = await this.sqliteStore.listRuns(sqlitePath, status.paths.statePath);
      return { runs: sqliteRuns, warnings: [...new Set(warnings)] };
    }

    const directories = status.paths.reportDirectories ?? [];
    const fileEntries = await Promise.all(
      directories.map(async (directory) => {
        try {
          const files = await fs.readdir(directory, { withFileTypes: true });
          return files
            .filter((entry) => entry.isFile() && entry.name.startsWith('syncfactors-') && entry.name.endsWith('.json'))
            .map((entry) => path.join(directory, entry.name));
        } catch {
          return [];
        }
      }),
    );

    const reportPaths = [...new Set(fileEntries.flat())];
    const scanned: ScannedRun[] = [];
    for (const reportPath of reportPaths) {
      try {
        const report = await this.readReport(reportPath);
        const run = this.findRunSummary(status, reportPath, report);
        scanned.push({ run, report });
      } catch (error) {
        warnings.push(`Skipped malformed report '${path.basename(reportPath)}'.`);
      }
    }

    scanned.sort(compareRunsDescending);
    return { runs: scanned, warnings: [...new Set(warnings)] };
  }

  private findRunSummary(status: DashboardStatus, reportPath: string, report: Record<string, unknown>): RunSummary {
    const existing = status.recentRuns.find((run) => run.path === reportPath);
    if (existing) {
      return existing;
    }

    return {
      runId: asString(report.runId),
      path: reportPath,
      artifactType: asString(report.artifactType) ?? 'SyncReport',
      workerScope: asRecord(report.workerScope) as RunSummary['workerScope'],
      configPath: asString(report.configPath),
      mappingConfigPath: asString(report.mappingConfigPath),
      mode: asString(report.mode),
      dryRun: Boolean(report.dryRun),
      status: asString(report.status),
      startedAt: asString(report.startedAt),
      completedAt: asString(report.completedAt),
      durationSeconds: getDurationSeconds(report),
      reversibleOperations: arrayOf(report.operations).length,
      creates: arrayOf(report.creates).length,
      updates: arrayOf(report.updates).length,
      enables: arrayOf(report.enables).length,
      disables: arrayOf(report.disables).length,
      graveyardMoves: arrayOf(report.graveyardMoves).length,
      deletions: arrayOf(report.deletions).length,
      quarantined: arrayOf(report.quarantined).length,
      conflicts: arrayOf(report.conflicts).length,
      guardrailFailures: arrayOf(report.guardrailFailures).length,
      manualReview: arrayOf(report.manualReview).length,
      unchanged: arrayOf(report.unchanged).length,
      reviewSummary: asRecord(report.reviewSummary),
    };
  }

  private findScannedRun(scannedRuns: ScannedRun[], runId: string): ScannedRun {
    const found = scannedRuns.find((entry) => entry.run.runId === runId);
    if (!found) {
      throw new Error(`Run '${runId}' was not found.`);
    }
    return found;
  }

  private async readReport(reportPath: string): Promise<Record<string, unknown>> {
    const stat = await fs.stat(reportPath);
    const cached = this.reportCache.get(reportPath);
    if (cached && cached.mtimeMs === stat.mtimeMs) {
      return cached.report;
    }

    const raw = await fs.readFile(reportPath, 'utf8');
    const report = JSON.parse(raw) as Record<string, unknown>;
    this.reportCache.set(reportPath, { mtimeMs: stat.mtimeMs, report });
    return report;
  }
}

function flattenEntries(run: RunSummary, report: Record<string, unknown>, trackedWorkers: TrackedWorker[]): EntryRecord[] {
  const operations = arrayOf(report.operations).map((operation) => asRecord(operation));
  const entries: EntryRecord[] = [];
  for (const bucket of BUCKET_ORDER) {
    const items = arrayOf(report[bucket]).map((item) => asRecord(item));
    for (let index = 0; index < items.length; index += 1) {
      const item = items[index];
      const workerId = asString(item.workerId);
      const diffRows = getDiffRows(item, operations, workerId, bucket);
      const trackedWorker = trackedWorkers.find((candidate) => candidate.workerId === workerId);
      const operation = operations.find((candidate) => asString(candidate.workerId) === workerId && asString(candidate.bucket) === bucket);
      const groupLabel = asString(item.reason) ?? asString(item.reviewCaseType) ?? run.artifactType;
      entries.push({
        entryId: buildEntryId(run, bucket, workerId, asString(item.samAccountName), index),
        runId: run.runId,
        reportPath: run.path,
        artifactType: run.artifactType,
        mode: run.mode,
        bucket,
        bucketLabel: BUCKET_LABELS[bucket] ?? bucket,
        queueName: getQueueName(bucket),
        workerId,
        samAccountName: asString(item.samAccountName),
        reason: asString(item.reason),
        reviewCategory: asString(item.reviewCategory),
        reviewCaseType: asString(item.reviewCaseType),
        groupKey: `${groupLabel.toLowerCase()}::${run.artifactType.toLowerCase()}`,
        groupLabel,
        operatorActionSummary: asString(item.operatorActionSummary),
        operatorActions: arrayOf(item.operatorActions) as EntryRecord['operatorActions'],
        targetOu: asString(item.targetOu),
        currentDistinguishedName:
          asString(item.currentDistinguishedName) ??
          asString(item.distinguishedName) ??
          trackedWorker?.distinguishedName ??
          null,
        currentEnabled: asBoolean(item.currentEnabled),
        proposedEnable: asBoolean(item.proposedEnable),
        matchedExistingUser: asBoolean(item.matchedExistingUser),
        changeCount: diffRows.filter((row) => row.changed).length,
        startedAt: run.startedAt,
        staleDays: getStaleDays(run.startedAt),
        operationSummary: getOperationSummary(bucket, item, operation),
        diffRows,
        item,
      });
    }
  }

  return entries.sort(compareEntriesDescending);
}

function materializeSqliteEntry(entry: SqliteEntryRecord, trackedWorkers: TrackedWorker[]): EntryRecord {
  const operations = arrayOf(entry.report.operations).map((operation) => asRecord(operation));
  const bucket = entry.row.bucket ?? 'unknown';
  const item = entry.item;
  const workerId = asString(item.workerId) ?? entry.row.worker_id ?? null;
  const trackedWorker = trackedWorkers.find((candidate) => candidate.workerId === workerId);
  const diffRows = getDiffRows(item, operations, workerId, bucket);
  const operation = operations.find((candidate) => asString(candidate.workerId) === workerId && asString(candidate.bucket) === bucket);
  const groupLabel = asString(item.reason) ?? asString(item.reviewCaseType) ?? entry.run.artifactType;

  return {
    entryId: entry.row.entry_id ?? buildEntryId(entry.run, bucket, workerId, asString(item.samAccountName), asInteger(entry.row.bucket_index) ?? 0),
    runId: entry.run.runId,
    reportPath: entry.run.path,
    artifactType: entry.run.artifactType,
    mode: entry.run.mode,
    bucket,
    bucketLabel: BUCKET_LABELS[bucket] ?? bucket,
    queueName: getQueueName(bucket),
    workerId,
    samAccountName: asString(item.samAccountName) ?? entry.row.sam_account_name ?? null,
    reason: asString(item.reason) ?? entry.row.reason ?? null,
    reviewCategory: asString(item.reviewCategory) ?? entry.row.review_category ?? null,
    reviewCaseType: asString(item.reviewCaseType) ?? entry.row.review_case_type ?? null,
    groupKey: `${groupLabel.toLowerCase()}::${entry.run.artifactType.toLowerCase()}`,
    groupLabel,
    operatorActionSummary: asString(item.operatorActionSummary),
    operatorActions: arrayOf(item.operatorActions) as EntryRecord['operatorActions'],
    targetOu: asString(item.targetOu),
    currentDistinguishedName:
      asString(item.currentDistinguishedName) ??
      asString(item.distinguishedName) ??
      trackedWorker?.distinguishedName ??
      null,
    currentEnabled: asBoolean(item.currentEnabled),
    proposedEnable: asBoolean(item.proposedEnable),
    matchedExistingUser: asBoolean(item.matchedExistingUser),
    changeCount: diffRows.filter((row) => row.changed).length,
    startedAt: entry.run.startedAt,
    staleDays: getStaleDays(entry.run.startedAt),
    operationSummary: getOperationSummary(bucket, item, operation),
    diffRows,
    item,
  };
}

function asInteger(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }
  if (typeof value === 'string' && value.trim()) {
    const parsed = Number.parseInt(value, 10);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

function matchesQueueEntry(
  entry: EntryRecord,
  filters: { reason?: string; reviewCaseType?: string; workerId?: string; filter?: string },
): boolean {
  if (filters.reason && `${entry.reason ?? ''}`.toLowerCase() !== filters.reason.toLowerCase()) {
    return false;
  }
  if (filters.reviewCaseType && `${entry.reviewCaseType ?? ''}`.toLowerCase() !== filters.reviewCaseType.toLowerCase()) {
    return false;
  }
  if (filters.workerId && entry.workerId !== filters.workerId) {
    return false;
  }
  if (filters.filter && !matchesFilter(entry, filters.filter)) {
    return false;
  }
  return true;
}

function matchesRunEntry(
  entry: EntryRecord,
  filters: { bucket?: string; workerId?: string; reason?: string; filter?: string; entryId?: string },
): boolean {
  if (filters.bucket && entry.bucket !== filters.bucket) {
    return false;
  }
  if (filters.workerId && entry.workerId !== filters.workerId) {
    return false;
  }
  if (filters.entryId && entry.entryId !== filters.entryId) {
    return false;
  }
  if (filters.reason && `${entry.reason ?? ''}`.toLowerCase() !== filters.reason.toLowerCase()) {
    return false;
  }
  if (filters.filter && !matchesFilter(entry, filters.filter)) {
    return false;
  }
  return true;
}

function getQueueName(bucket: string): QueueName | null {
  if (bucket === 'manualReview') {
    return 'manual-review';
  }
  if (bucket === 'quarantined') {
    return 'quarantined';
  }
  if (bucket === 'conflicts') {
    return 'conflicts';
  }
  if (bucket === 'guardrailFailures') {
    return 'guardrails';
  }
  return null;
}

function buildEntryId(run: RunSummary, bucket: string, workerId: string | null, samAccountName: string | null, index: number): string {
  return [run.runId ?? 'no-run', bucket, workerId ?? samAccountName ?? 'unknown', String(index)].join(':');
}

function getDiffRows(item: Record<string, unknown>, operations: Record<string, unknown>[], workerId: string | null, bucket: string): DiffRow[] {
  const changedRows = arrayOf(item.changedAttributeDetails).map((row) => asRecord(row)).map((row) => ({
    attribute: asString(row.targetAttribute) ?? 'attribute',
    source: asString(row.sourceField),
    before: inlineValue(row.currentAdValue),
    after: inlineValue(row.proposedValue),
    changed: true,
  }));

  if (changedRows.length > 0) {
    return changedRows;
  }

  const attributeRows = arrayOf(item.attributeRows)
    .map((row) => asRecord(row))
    .map((row) => ({
      attribute: asString(row.targetAttribute) ?? 'attribute',
      source: asString(row.sourceField),
      before: inlineValue(row.currentAdValue),
      after: inlineValue(row.proposedValue),
      changed: Boolean(row.changed),
    }));
  if (attributeRows.length > 0) {
    return attributeRows;
  }

  const operation = operations.find((candidate) => asString(candidate.workerId) === workerId && asString(candidate.bucket) === bucket);
  if (!operation) {
    return [];
  }

  const before = asRecord(operation.before);
  const after = asRecord(operation.after);
  const keys = [...new Set([...Object.keys(before ?? {}), ...Object.keys(after ?? {})])];
  return keys.map((key) => ({
    attribute: key,
    source: null,
    before: inlineValue(before?.[key]),
    after: inlineValue(after?.[key]),
    changed: inlineValue(before?.[key]) !== inlineValue(after?.[key]),
  }));
}

function getOperationSummary(
  bucket: string,
  item: Record<string, unknown>,
  operation: Record<string, unknown> | undefined,
): OperationSummary | null {
  const targetSam = asString(operation?.target && asRecord(operation.target)?.samAccountName) ?? asString(item.samAccountName) ?? 'user';
  const operationType = asString(operation?.operationType);
  switch (operationType) {
    case 'DisableUser':
      return { action: `Disable account ${targetSam}`, effect: 'Account sign-in will be turned off.', targetOu: null, fromOu: null, toOu: null };
    case 'EnableUser':
      return { action: `Enable account ${targetSam}`, effect: 'Account sign-in will be turned on.', targetOu: null, fromOu: null, toOu: null };
    case 'MoveUser':
      return {
        action: `Move account ${targetSam}`,
        effect: null,
        targetOu: asString(asRecord(operation?.after)?.targetOu) ?? asString(item.targetOu),
        fromOu: asString(asRecord(operation?.before)?.parentOu),
        toOu: asString(asRecord(operation?.after)?.targetOu) ?? asString(item.targetOu),
      };
    case 'CreateUser':
      return {
        action: `Create account ${targetSam}`,
        effect: null,
        targetOu: asString(asRecord(operation?.after)?.targetOu) ?? asString(item.targetOu),
        fromOu: null,
        toOu: null,
      };
    case 'DeleteUser':
      return { action: `Delete account ${targetSam}`, effect: 'The AD user object will be removed.', targetOu: null, fromOu: null, toOu: null };
    case 'UpdateAttributes':
      return {
        action: `Update attributes for ${targetSam}`,
        effect: `${getDiffRows(item, operation ? [operation] : [], asString(item.workerId), bucket).filter((row) => row.changed).length} attribute changes.`,
        targetOu: null,
        fromOu: null,
        toOu: null,
      };
    default:
      if (bucket === 'quarantined' || bucket === 'manualReview' || bucket === 'conflicts' || bucket === 'guardrailFailures') {
        return { action: BUCKET_LABELS[bucket] ?? bucket, effect: asString(item.reason) ?? asString(item.reviewCaseType), targetOu: null, fromOu: null, toOu: null };
      }
      return null;
  }
}

function buildGroups(entries: EntryRecord[], selector: (entry: EntryRecord) => string): QueueGroup[] {
  const counts = new Map<string, number>();
  for (const entry of entries) {
    const key = selector(entry) || 'Other';
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }

  return [...counts.entries()]
    .map(([key, count]) => ({ key, label: key, count }))
    .sort((a, b) => (b.count - a.count) || a.label.localeCompare(b.label));
}

function matchesFilter(entry: EntryRecord, filter: string): boolean {
  const needle = filter.trim().toLowerCase();
  if (!needle) {
    return true;
  }

  const haystack = JSON.stringify({
    workerId: entry.workerId,
    samAccountName: entry.samAccountName,
    reason: entry.reason,
    reviewCategory: entry.reviewCategory,
    reviewCaseType: entry.reviewCaseType,
    artifactType: entry.artifactType,
    bucketLabel: entry.bucketLabel,
    item: entry.item,
  }).toLowerCase();

  return haystack.includes(needle);
}

function compareRunsDescending(left: ScannedRun, right: ScannedRun): number {
  return compareStringsDesc(left.run.startedAt, right.run.startedAt) || compareStringsDesc(left.run.path, right.run.path);
}

function compareEntriesDescending(left: EntryRecord, right: EntryRecord): number {
  return compareStringsDesc(left.startedAt, right.startedAt) || left.entryId.localeCompare(right.entryId);
}

function compareStringsDesc(left: string | null | undefined, right: string | null | undefined): number {
  const leftValue = left ?? '';
  const rightValue = right ?? '';
  return rightValue.localeCompare(leftValue);
}

function getDurationSeconds(report: Record<string, unknown>): number | null {
  const startedAt = asString(report.startedAt);
  const completedAt = asString(report.completedAt);
  if (!startedAt || !completedAt) {
    return null;
  }

  const start = Date.parse(startedAt);
  const end = Date.parse(completedAt);
  if (Number.isNaN(start) || Number.isNaN(end)) {
    return null;
  }

  return Math.max(Math.round((end - start) / 1000), 0);
}

function getStaleDays(startedAt: string | null): number | null {
  if (!startedAt) {
    return null;
  }

  const time = Date.parse(startedAt);
  if (Number.isNaN(time)) {
    return null;
  }

  return Math.max(Math.floor((Date.now() - time) / 86400000), 0);
}

function inlineValue(value: unknown): string {
  if (value === null || value === undefined || value === '') {
    return '(unset)';
  }
  if (Array.isArray(value)) {
    return value.map((item) => inlineValue(item)).join(', ');
  }
  if (typeof value === 'object') {
    return JSON.stringify(value);
  }
  return String(value);
}

function arrayOf(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function asString(value: unknown): string | null {
  return typeof value === 'string' && value.trim() ? value : null;
}

function asBoolean(value: unknown): boolean | null {
  return typeof value === 'boolean' ? value : null;
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : {};
}

function isReviewCreated(entry: EntryRecord): boolean {
  return entry.bucket === 'creates' || entry.reviewCategory === 'NewUser';
}

function isReviewChanged(entry: EntryRecord): boolean {
  return ['updates', 'enables', 'disables', 'graveyardMoves', 'unchanged'].includes(entry.bucket);
}

function isReviewDeleted(entry: EntryRecord): boolean {
  return ['deletions', 'manualReview', 'quarantined', 'conflicts', 'guardrailFailures'].includes(entry.bucket);
}
