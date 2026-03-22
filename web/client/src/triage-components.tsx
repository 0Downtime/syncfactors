import { useEffect, useMemo, useState } from 'react';
import type { MutableRefObject, ReactNode } from 'react';
import type { DashboardStatus, EntryRecord, QueueGroup } from './types.js';

export function getToneForBucket(bucket: string | null | undefined): string {
  switch (bucket) {
    case 'creates':
      return 'create';
    case 'updates':
      return 'update';
    case 'deletions':
      return 'delete';
    case 'quarantined':
      return 'quarantine';
    case 'manualReview':
    case 'manual-review':
      return 'review';
    case 'conflicts':
      return 'conflict';
    case 'guardrailFailures':
    case 'guardrails':
      return 'guardrail';
    case 'unchanged':
      return 'neutral';
    default:
      return 'default';
  }
}

export function getToneForReviewExplorer(mode: 'all' | 'changed' | 'created' | 'deleted'): string {
  switch (mode) {
    case 'all':
      return 'all';
    case 'changed':
      return 'update';
    case 'created':
      return 'create';
    case 'deleted':
      return 'delete';
  }
}

export function getToneForReviewEntry(entry: Pick<EntryRecord, 'bucket' | 'reviewCategory'>): string {
  if (entry.bucket === 'creates' || entry.reviewCategory === 'NewUser') {
    return 'create';
  }
  if (['updates', 'enables', 'disables', 'graveyardMoves', 'unchanged'].includes(entry.bucket)) {
    return 'update';
  }
  if (['deletions', 'manualReview', 'quarantined', 'conflicts', 'guardrailFailures'].includes(entry.bucket)) {
    return 'delete';
  }
  return getToneForBucket(entry.bucket);
}

export function SelectedEntryPanel(props: {
  entry: EntryRecord | null;
  diffMode: 'changed' | 'all';
  onDiffModeChange: (mode: 'changed' | 'all') => void;
  onOpenWorker: (workerId: string) => void;
  compact?: boolean;
  tone?: string;
}) {
  const { entry, diffMode, onDiffModeChange, onOpenWorker, compact = false, tone } = props;
  if (!entry) {
    return <div className="selected-panel empty-state">No entry selected.</div>;
  }

  const panelTone = tone ?? getToneForBucket(entry.bucket);

  const visibleDiffRows = diffMode === 'all' ? entry.diffRows : entry.diffRows.filter((row) => row.changed);
  return (
    <div className="selected-panel" data-tone={panelTone}>
      <div className="selected-header">
        <div>
          <p className="section-kicker">Selected Object</p>
          <h3>{entry.workerId ?? entry.samAccountName ?? 'Unknown object'}</h3>
        </div>
        <div className="header-actions">
          <span className="badge" data-tone={panelTone}>{entry.bucketLabel}</span>
          {entry.workerId && !compact ? <button type="button" onClick={() => onOpenWorker(entry.workerId!)}>Worker page</button> : null}
        </div>
      </div>

      <dl className="detail-list">
        <DetailRow label="Reason" value={entry.reason ?? '-'} />
        <DetailRow label="Review category" value={entry.reviewCategory ?? '-'} />
        <DetailRow label="Review case" value={entry.reviewCaseType ?? '-'} />
        <DetailRow label="SamAccountName" value={entry.samAccountName ?? '-'} />
        <DetailRow label="Target OU" value={entry.targetOu ?? '-'} />
        <DetailRow label="Current DN" value={entry.currentDistinguishedName ?? '-'} />
      </dl>

      {entry.operationSummary ? (
        <section className="operator-panel">
          <h4>Operation summary</h4>
          <p><strong>{entry.operationSummary.action}</strong></p>
          {entry.operationSummary.effect ? <p>{entry.operationSummary.effect}</p> : null}
          {(entry.operationSummary.fromOu || entry.operationSummary.toOu) ? (
            <p>From {entry.operationSummary.fromOu ?? '-'} to {entry.operationSummary.toOu ?? '-'}</p>
          ) : null}
        </section>
      ) : null}

      {entry.operatorActionSummary ? (
        <section className="operator-panel">
          <h4>Manual review workflow</h4>
          <p>{entry.operatorActionSummary}</p>
          {entry.operatorActions.length > 0 ? (
            <ul>
              {entry.operatorActions.map((action, index) => (
                <li key={`${action.code ?? action.label ?? index}`}>
                  <strong>{action.label ?? action.code ?? 'Action'}</strong>: {action.description ?? 'No description provided.'}
                </li>
              ))}
            </ul>
          ) : null}
        </section>
      ) : null}

      <section className="changes-panel">
        <div className="changes-header">
          <h4>Structured diff</h4>
          {!compact ? (
            <div className="toggle-row">
              <button type="button" className={diffMode === 'changed' ? 'active' : ''} onClick={() => onDiffModeChange('changed')}>Changed only</button>
              <button type="button" className={diffMode === 'all' ? 'active' : ''} onClick={() => onDiffModeChange('all')}>All rows</button>
            </div>
          ) : null}
        </div>
        {visibleDiffRows.length === 0 ? (
          <p>No attribute-level differences were recorded.</p>
        ) : (
          <table className="diff-table">
            <thead>
              <tr>
                <th>Attribute</th>
                <th>Source</th>
                <th>Current</th>
                <th>Proposed</th>
              </tr>
            </thead>
            <tbody>
              {visibleDiffRows.map((row) => (
                <tr key={`${row.attribute}-${row.source ?? 'none'}`} className={row.changed ? 'changed' : ''}>
                  <td>{row.attribute}</td>
                  <td>{row.source ?? '-'}</td>
                  <td>
                    <span className="diff-value diff-value-remove">
                      <span className="diff-value-prefix" aria-hidden="true">-</span>
                      <span>{row.before}</span>
                    </span>
                  </td>
                  <td>
                    <span className="diff-value diff-value-add">
                      <span className="diff-value-prefix" aria-hidden="true">+</span>
                      <span>{row.after}</span>
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}

export function GroupPanel(props: { title: string; groups: QueueGroup[]; activeKey: string; onSelect: (key: string) => void; disabled?: boolean }) {
  return (
    <section className="group-panel">
      <h3>{props.title}</h3>
      <div className="group-list">
        <button type="button" className={!props.activeKey ? 'active' : ''} onClick={() => props.onSelect('')} disabled={props.disabled}>All</button>
        {props.groups.map((group) => (
          <button
            key={group.key}
            type="button"
            className={props.activeKey === group.key ? 'active' : ''}
            onClick={() => props.onSelect(group.key)}
            disabled={props.disabled}
          >
            {group.label} ({group.count})
          </button>
        ))}
      </div>
    </section>
  );
}

export function WarningPanel({ title, warnings }: { title: string; warnings: string[] }) {
  return (
    <section className="warning-panel">
      <strong>{title}</strong>
      <ul>
        {warnings.map((warning) => <li key={warning}>{warning}</li>)}
      </ul>
    </section>
  );
}

export function StatusNote({ children }: { children: ReactNode }) {
  return <section className="status-note">{children}</section>;
}

export function StatusPanel({ title, health }: { title: string; health?: { status: string; detail: string } }) {
  return (
    <section className="card status-card">
      <p className="section-kicker">{title}</p>
      <h2>{health?.status ?? 'UNKNOWN'}</h2>
      <ExpandableText text={health?.detail ?? 'Waiting for probe details.'} />
    </section>
  );
}

export function SummaryPanel({ status }: { status: DashboardStatus | null }) {
  return (
    <section className="card status-card">
      <p className="section-kicker">State Summary</p>
      <h2>{status?.summary.totalTrackedWorkers ?? 0} tracked workers</h2>
      <p className="status-inline-row">Suppressed {status?.summary.suppressedWorkers ?? 0} | Pending deletion {status?.summary.pendingDeletionWorkers ?? 0}</p>
      <ExpandableText text={`Checkpoint ${status?.summary.lastCheckpoint ?? 'none'}`} />
    </section>
  );
}

export function CurrentRunPanel({ currentRun }: { currentRun: Record<string, unknown> | null }) {
  return (
    <section className="card current-run-card">
      <p className="section-kicker">Current Run</p>
      <h2>{`${currentRun?.status ?? 'Idle'} / ${currentRun?.stage ?? 'Completed'}`}</h2>
      <ExpandableText text={`${currentRun?.lastAction ?? 'No active sync run.'}`} />
      <p className="status-inline-row">Progress {`${currentRun?.processedWorkers ?? 0}`} / {`${currentRun?.totalWorkers ?? 0}`} | Worker {`${currentRun?.currentWorkerId ?? '-'}`}</p>
    </section>
  );
}

export function DashboardOverviewPanel({ status }: { status: DashboardStatus | null }) {
  const successFactors = status?.health.successFactors;
  const activeDirectory = status?.health.activeDirectory;
  const currentRun = status?.currentRun ?? null;
  const totalWorkers = Number(currentRun?.totalWorkers ?? 0);
  const processedWorkers = Number(currentRun?.processedWorkers ?? 0);
  const progressPercent = totalWorkers > 0 ? Math.max(0, Math.min(100, (processedWorkers / totalWorkers) * 100)) : 0;

  return (
    <section className="card overview-card">
      <div className="overview-header">
        <div>
          <p className="section-kicker">Operations Overview</p>
          <h2>System state</h2>
        </div>
        <span className="overview-live-pill">Live</span>
      </div>

      <div className="overview-compact-grid">
        <div className="overview-chip-grid">
          <OverviewStatusChip label="SF" health={successFactors} />
          <OverviewStatusChip label="AD" health={activeDirectory} />
          <OverviewMetricChip label="WK" value={`${status?.summary.totalTrackedWorkers ?? 0}`} />
          <OverviewMetricChip label="DEL" value={`${status?.summary.pendingDeletionWorkers ?? 0}`} />
        </div>

        <div className="overview-run-compact">
          <div className="overview-run-compact-head">
            <div className="overview-run-titleline">
              <span className="overview-run-symbol" aria-hidden="true">◔</span>
              <strong>{`${currentRun?.status ?? 'Idle'} / ${currentRun?.stage ?? 'Completed'}`}</strong>
            </div>
            <span className="overview-run-percent">{Math.round(progressPercent)}%</span>
          </div>
          <div className="overview-progress-track" aria-hidden="true">
            <span className="overview-progress-fill" style={{ width: `${progressPercent}%` }} />
          </div>
          <div className="overview-run-meta compact">
            <span>{processedWorkers}/{totalWorkers || 0}</span>
            <span>W {`${currentRun?.currentWorkerId ?? '-'}`}</span>
            <span>S {status?.summary.suppressedWorkers ?? 0}</span>
            <span className="overview-checkpoint">
              CP <ExpandableText text={status?.summary.lastCheckpoint ?? 'none'} maxInlineLength={18} />
            </span>
          </div>
        </div>
      </div>
    </section>
  );
}

export function RelativeTimeLabel(props: { timestamp: string | null; emptyLabel?: string; className?: string }) {
  const { timestamp, emptyLabel = '-', className } = props;
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const interval = window.setInterval(() => setNow(Date.now()), 30000);
    return () => window.clearInterval(interval);
  }, []);

  const relativeLabel = useMemo(() => formatRelativeTime(timestamp, now), [timestamp, now]);
  return <span className={className}>{relativeLabel ?? emptyLabel}</span>;
}

export function AbsoluteTimeLabel(props: { timestamp: string | null; emptyLabel?: string; className?: string }) {
  const { timestamp, emptyLabel = '-', className } = props;
  const formattedAbsolute = useMemo(() => formatRunTimestamp(timestamp), [timestamp]);
  return <span className={className}>{formattedAbsolute ?? emptyLabel}</span>;
}

export function formatRunTimestamp(timestamp: string | null): string | null {
  if (!timestamp) {
    return null;
  }

  const parsed = new Date(timestamp);
  if (Number.isNaN(parsed.getTime())) {
    return timestamp;
  }

  return new Intl.DateTimeFormat(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
    second: '2-digit',
  }).format(parsed);
}

export function formatRelativeTime(timestamp: string | null, now: number): string | null {
  if (!timestamp) {
    return null;
  }

  const parsed = new Date(timestamp).getTime();
  if (Number.isNaN(parsed)) {
    return null;
  }

  const diffMs = now - parsed;
  const tense = diffMs >= 0 ? 'ago' : 'from now';
  const absoluteMs = Math.abs(diffMs);
  const minute = 60 * 1000;
  const hour = 60 * minute;
  const day = 24 * hour;
  const week = 7 * day;
  const month = 30 * day;
  const year = 365 * day;

  if (absoluteMs < minute) {
    return tense === 'ago' ? 'less than a minute ago' : 'in less than a minute';
  }

  const units = [
    { label: 'year', size: year },
    { label: 'month', size: month },
    { label: 'week', size: week },
    { label: 'day', size: day },
    { label: 'hour', size: hour },
    { label: 'minute', size: minute },
  ];

  const unit = units.find((entry) => absoluteMs >= entry.size) ?? units[units.length - 1];
  const count = Math.floor(absoluteMs / unit.size);
  const suffix = count === 1 ? '' : 's';
  return tense === 'ago' ? `${count} ${unit.label}${suffix} ago` : `in ${count} ${unit.label}${suffix}`;
}

export function SummaryMetric({ label, value }: { label: string; value: string }) {
  return (
    <div className="metric">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

export function DetailRow({ label, value }: { label: string; value: string }) {
  return (
    <>
      <dt>{label}</dt>
      <dd>{value}</dd>
    </>
  );
}

function ExpandableText(props: { text: string; maxInlineLength?: number }) {
  const { text, maxInlineLength = 88 } = props;
  const [expanded, setExpanded] = useState(false);
  const shouldExpand = text.length > maxInlineLength || text.includes('\n');

  return (
    <>
      <div className="compact-text-row">
        <p className="compact-text" title={text}>{text}</p>
        {shouldExpand ? (
          <button className="inline-expand-button" onClick={() => setExpanded(true)} type="button">
            Expand
          </button>
        ) : null}
      </div>
      {expanded ? (
        <div className="detail-modal-backdrop" onClick={() => setExpanded(false)} role="presentation">
          <div
            aria-labelledby="detail-modal-title"
            aria-modal="true"
            className="detail-modal"
            onClick={(event) => event.stopPropagation()}
            role="dialog"
          >
            <div className="detail-modal-header">
              <strong id="detail-modal-title">Full detail</strong>
              <button className="inline-expand-button" onClick={() => setExpanded(false)} type="button">
                Close
              </button>
            </div>
            <pre className="detail-modal-content">{text}</pre>
          </div>
        </div>
      ) : null}
    </>
  );
}

function OverviewStatusTile(props: { label: string; health?: { status: string; detail: string } }) {
  const status = props.health?.status ?? 'UNKNOWN';
  const tone = getHealthTone(status);

  return (
    <div className="overview-status-tile" data-health={tone}>
      <div className="overview-status-header">
        <span>{props.label}</span>
        <span className="overview-status-pill">{status}</span>
      </div>
      <ExpandableText text={props.health?.detail ?? 'Waiting for probe details.'} maxInlineLength={56} />
    </div>
  );
}

function OverviewStatusChip(props: { label: string; health?: { status: string; detail: string } }) {
  const status = props.health?.status ?? 'UNKNOWN';
  const tone = getHealthTone(status);
  const symbol = tone === 'good' ? '●' : tone === 'warning' ? '▲' : tone === 'critical' ? '■' : '○';

  return (
    <div className="overview-chip" data-health={tone} title={`${props.label}: ${status}`}>
      <span className="overview-chip-label">{props.label}</span>
      <span className="overview-chip-value">{symbol} {status}</span>
    </div>
  );
}

function OverviewMetricChip(props: { label: string; value: string }) {
  return (
    <div className="overview-chip metric">
      <span className="overview-chip-label">{props.label}</span>
      <span className="overview-chip-value">{props.value}</span>
    </div>
  );
}

function getHealthTone(status: string): 'good' | 'warning' | 'critical' | 'unknown' {
  const normalized = status.toLowerCase();
  if (normalized === 'ok' || normalized === 'healthy' || normalized === 'succeeded') {
    return 'good';
  }
  if (normalized === 'warning' || normalized === 'degraded') {
    return 'warning';
  }
  if (normalized === 'error' || normalized === 'failed' || normalized === 'down') {
    return 'critical';
  }
  return 'unknown';
}

export function CopyLinkButton({ label }: { label: string }) {
  return (
    <button
      className="copy-link-button"
      type="button"
      onClick={() => {
        void navigator.clipboard?.writeText(window.location.href);
      }}
    >
      {label}
    </button>
  );
}

export type FilterRef = MutableRefObject<HTMLInputElement | null>;
