import { execFile } from 'node:child_process';
import fs from 'node:fs/promises';
import { promisify } from 'node:util';
import type { EntryRecord, QueueName, RunSummary } from './types.js';

const execFileAsync = promisify(execFile);

type SqliteRunRow = {
  run_id?: string | null;
  path?: string | null;
  artifact_type?: string | null;
  worker_scope_json?: string | null;
  config_path?: string | null;
  mapping_config_path?: string | null;
  mode?: string | null;
  dry_run?: number | string | null;
  status?: string | null;
  started_at?: string | null;
  completed_at?: string | null;
  duration_seconds?: number | string | null;
  reversible_operations?: number | string | null;
  creates?: number | string | null;
  updates?: number | string | null;
  enables?: number | string | null;
  disables?: number | string | null;
  graveyard_moves?: number | string | null;
  deletions?: number | string | null;
  quarantined?: number | string | null;
  conflicts?: number | string | null;
  guardrail_failures?: number | string | null;
  manual_review?: number | string | null;
  unchanged?: number | string | null;
  review_summary_json?: string | null;
  report_json?: string | null;
};

export type SqliteScannedRun = {
  run: RunSummary;
  report: Record<string, unknown>;
};

type SqliteEntryRow = SqliteRunRow & {
  entry_id?: string | null;
  bucket?: string | null;
  bucket_index?: number | string | null;
  worker_id?: string | null;
  sam_account_name?: string | null;
  reason?: string | null;
  review_category?: string | null;
  review_case_type?: string | null;
  item_json?: string | null;
};

export type SqliteEntryRecord = {
  row: SqliteEntryRow;
  run: RunSummary;
  report: Record<string, unknown>;
  item: Record<string, unknown>;
};

export class SqliteStore {
  async listRuns(sqlitePath: string, statePath: string): Promise<SqliteScannedRun[]> {
    if (!(await this.exists(sqlitePath))) {
      return [];
    }

    const rows = await this.query<SqliteRunRow>(
      sqlitePath,
      `
SELECT
  run_id,
  path,
  artifact_type,
  worker_scope_json,
  config_path,
  mapping_config_path,
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
  review_summary_json,
  report_json
FROM runs
WHERE state_path = ${quote(statePath)}
ORDER BY COALESCE(started_at, '') DESC, COALESCE(path, '') DESC;
      `,
    );

    return rows.flatMap((row) => {
      const scanned = mapRunRow(row);
      return scanned ? [scanned] : [];
    });
  }

  async getQueueEntries(
    sqlitePath: string,
    statePath: string,
    queueName: QueueName,
    filters: { reason?: string; reviewCaseType?: string; workerId?: string; filter?: string },
  ): Promise<SqliteEntryRecord[]> {
    const buckets = getQueueBuckets(queueName);
    if (!(await this.exists(sqlitePath)) || buckets.length === 0) {
      return [];
    }

    const where = [
      `e.state_path = ${quote(statePath)}`,
      `e.bucket IN (${buckets.map((bucket) => quote(bucket)).join(', ')})`,
    ];
    if (filters.reason) {
      where.push(`LOWER(COALESCE(e.reason, '')) = LOWER(${quote(filters.reason)})`);
    }
    if (filters.reviewCaseType) {
      where.push(`LOWER(COALESCE(e.review_case_type, '')) = LOWER(${quote(filters.reviewCaseType)})`);
    }
    if (filters.workerId) {
      where.push(`e.worker_id = ${quote(filters.workerId)}`);
    }
    if (filters.filter) {
      const needle = `%${escapeLike(filters.filter.toLowerCase())}%`;
      where.push(`LOWER(COALESCE(e.item_json, '')) LIKE ${quote(needle)} ESCAPE '\\'`);
    }

    const rows = await this.query<SqliteEntryRow>(
      sqlitePath,
      `
SELECT
  e.entry_id,
  e.bucket,
  e.bucket_index,
  e.worker_id,
  e.sam_account_name,
  e.reason,
  e.review_category,
  e.review_case_type,
  e.item_json,
  r.run_id,
  r.path,
  r.artifact_type,
  r.worker_scope_json,
  r.config_path,
  r.mapping_config_path,
  r.mode,
  r.dry_run,
  r.status,
  r.started_at,
  r.completed_at,
  r.duration_seconds,
  r.reversible_operations,
  r.creates,
  r.updates,
  r.enables,
  r.disables,
  r.graveyard_moves,
  r.deletions,
  r.quarantined,
  r.conflicts,
  r.guardrail_failures,
  r.manual_review,
  r.unchanged,
  r.review_summary_json,
  r.report_json
FROM run_entries e
JOIN runs r ON r.run_id = e.run_id
WHERE ${where.join('\n  AND ')}
ORDER BY COALESCE(r.started_at, '') DESC, e.entry_id ASC;
      `,
    );

    return rows.flatMap((row) => mapEntryRow(row));
  }

  async getWorkerEntries(
    sqlitePath: string,
    statePath: string,
    workerId: string,
    limit: number,
  ): Promise<SqliteEntryRecord[]> {
    if (!(await this.exists(sqlitePath))) {
      return [];
    }

    const rows = await this.query<SqliteEntryRow>(
      sqlitePath,
      `
SELECT
  e.entry_id,
  e.bucket,
  e.bucket_index,
  e.worker_id,
  e.sam_account_name,
  e.reason,
  e.review_category,
  e.review_case_type,
  e.item_json,
  r.run_id,
  r.path,
  r.artifact_type,
  r.worker_scope_json,
  r.config_path,
  r.mapping_config_path,
  r.mode,
  r.dry_run,
  r.status,
  r.started_at,
  r.completed_at,
  r.duration_seconds,
  r.reversible_operations,
  r.creates,
  r.updates,
  r.enables,
  r.disables,
  r.graveyard_moves,
  r.deletions,
  r.quarantined,
  r.conflicts,
  r.guardrail_failures,
  r.manual_review,
  r.unchanged,
  r.review_summary_json,
  r.report_json
FROM run_entries e
JOIN runs r ON r.run_id = e.run_id
WHERE e.state_path = ${quote(statePath)}
  AND e.worker_id = ${quote(workerId)}
ORDER BY COALESCE(r.started_at, '') DESC, e.entry_id ASC
LIMIT ${Math.max(limit, 1)};
      `,
    );

    return rows.flatMap((row) => mapEntryRow(row));
  }

  async getRun(sqlitePath: string, runId: string): Promise<SqliteScannedRun | null> {
    if (!(await this.exists(sqlitePath))) {
      return null;
    }

    const rows = await this.query<SqliteRunRow>(
      sqlitePath,
      `
SELECT
  run_id,
  path,
  artifact_type,
  worker_scope_json,
  config_path,
  mapping_config_path,
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
  review_summary_json,
  report_json
FROM runs
WHERE run_id = ${quote(runId)}
LIMIT 1;
      `,
    );

    if (rows.length === 0) {
      return null;
    }

    return mapRunRow(rows[0]) ?? null;
  }

  async getRunEntries(
    sqlitePath: string,
    runId: string,
    filters: { bucket?: string; workerId?: string; reason?: string; filter?: string; entryId?: string },
  ): Promise<SqliteEntryRecord[]> {
    if (!(await this.exists(sqlitePath))) {
      return [];
    }

    const where = [`e.run_id = ${quote(runId)}`];
    if (filters.bucket) {
      where.push(`e.bucket = ${quote(filters.bucket)}`);
    }
    if (filters.workerId) {
      where.push(`e.worker_id = ${quote(filters.workerId)}`);
    }
    if (filters.reason) {
      where.push(`LOWER(COALESCE(e.reason, '')) = LOWER(${quote(filters.reason)})`);
    }
    if (filters.entryId) {
      where.push(`e.entry_id = ${quote(filters.entryId)}`);
    }
    if (filters.filter) {
      const needle = `%${escapeLike(filters.filter.toLowerCase())}%`;
      where.push(`LOWER(COALESCE(e.item_json, '')) LIKE ${quote(needle)} ESCAPE '\\'`);
    }

    const rows = await this.query<SqliteEntryRow>(
      sqlitePath,
      `
SELECT
  e.entry_id,
  e.bucket,
  e.bucket_index,
  e.worker_id,
  e.sam_account_name,
  e.reason,
  e.review_category,
  e.review_case_type,
  e.item_json,
  r.run_id,
  r.path,
  r.artifact_type,
  r.worker_scope_json,
  r.config_path,
  r.mapping_config_path,
  r.mode,
  r.dry_run,
  r.status,
  r.started_at,
  r.completed_at,
  r.duration_seconds,
  r.reversible_operations,
  r.creates,
  r.updates,
  r.enables,
  r.disables,
  r.graveyard_moves,
  r.deletions,
  r.quarantined,
  r.conflicts,
  r.guardrail_failures,
  r.manual_review,
  r.unchanged,
  r.review_summary_json,
  r.report_json
FROM run_entries e
JOIN runs r ON r.run_id = e.run_id
WHERE ${where.join('\n  AND ')}
ORDER BY e.bucket ASC, e.bucket_index ASC, e.entry_id ASC;
      `,
    );

    return rows.flatMap((row) => mapEntryRow(row));
  }

  private async query<T>(sqlitePath: string, sql: string): Promise<T[]> {
    const { stdout } = await execFileAsync('sqlite3', ['-json', '-cmd', '.timeout 5000', sqlitePath, sql], {
      maxBuffer: 1024 * 1024 * 20,
    });

    const trimmed = stdout.trim();
    if (!trimmed) {
      return [];
    }

    return JSON.parse(trimmed) as T[];
  }

  private async exists(targetPath: string): Promise<boolean> {
    try {
      await fs.access(targetPath);
      return true;
    } catch {
      return false;
    }
  }
}

function quote(value: string): string {
  return `'${value.replaceAll("'", "''")}'`;
}

function coerceNumber(value: number | string | null | undefined): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }
  if (typeof value === 'string' && value.trim()) {
    const parsed = Number.parseInt(value, 10);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

function coerceBoolean(value: number | string | null | undefined): boolean {
  if (typeof value === 'number') {
    return value !== 0;
  }
  return value === '1' || value === 'true';
}

function parseJsonRecord(value: string | null | undefined): Record<string, unknown> | null {
  if (!value) {
    return null;
  }

  try {
    const parsed = JSON.parse(value) as Record<string, unknown>;
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

function mapRunRow(row: SqliteRunRow): SqliteScannedRun | null {
  if (!row.report_json) {
    return null;
  }

  try {
    const report = JSON.parse(row.report_json) as Record<string, unknown>;
    return {
      run: {
        runId: row.run_id ?? null,
        path: row.path ?? null,
        artifactType: row.artifact_type ?? 'SyncReport',
        workerScope: parseJsonRecord(row.worker_scope_json),
        configPath: row.config_path ?? null,
        mappingConfigPath: row.mapping_config_path ?? null,
        mode: row.mode ?? null,
        dryRun: coerceBoolean(row.dry_run),
        status: row.status ?? null,
        startedAt: row.started_at ?? null,
        completedAt: row.completed_at ?? null,
        durationSeconds: coerceNumber(row.duration_seconds),
        reversibleOperations: coerceNumber(row.reversible_operations) ?? 0,
        creates: coerceNumber(row.creates) ?? 0,
        updates: coerceNumber(row.updates) ?? 0,
        enables: coerceNumber(row.enables) ?? 0,
        disables: coerceNumber(row.disables) ?? 0,
        graveyardMoves: coerceNumber(row.graveyard_moves) ?? 0,
        deletions: coerceNumber(row.deletions) ?? 0,
        quarantined: coerceNumber(row.quarantined) ?? 0,
        conflicts: coerceNumber(row.conflicts) ?? 0,
        guardrailFailures: coerceNumber(row.guardrail_failures) ?? 0,
        manualReview: coerceNumber(row.manual_review) ?? 0,
        unchanged: coerceNumber(row.unchanged) ?? 0,
        reviewSummary: parseJsonRecord(row.review_summary_json),
      },
      report,
    };
  } catch {
    return null;
  }
}

function mapEntryRow(row: SqliteEntryRow): SqliteEntryRecord[] {
  const scanned = mapRunRow(row);
  if (!scanned || !row.item_json) {
    return [];
  }

  try {
    return [{
      row,
      run: scanned.run,
      report: scanned.report,
      item: JSON.parse(row.item_json) as Record<string, unknown>,
    }];
  } catch {
    return [];
  }
}

function getQueueBuckets(queueName: QueueName): string[] {
  switch (queueName) {
    case 'manual-review':
      return ['manualReview'];
    case 'quarantined':
      return ['quarantined'];
    case 'conflicts':
      return ['conflicts'];
    case 'guardrails':
      return ['guardrailFailures'];
    default:
      return [];
  }
}

function escapeLike(value: string): string {
  return value.replaceAll('\\', '\\\\').replaceAll('%', '\\%').replaceAll('_', '\\_');
}
