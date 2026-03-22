import type { DashboardStatus, EntryListResponse, EntryRecord, OperatorActionKind, QueueName, QueueResponse, RunDetailResponse, WorkerActionKind, WorkerActionResponse, WorkerDetailResponse } from './types.js';
import type { RouteState } from './route-state.js';
import { mapReviewExplorerToBucket } from './route-state.js';
import { AbsoluteTimeLabel, CopyLinkButton, DashboardOverviewPanel, DetailRow, GroupPanel, RelativeTimeLabel, SelectedEntryPanel, SummaryMetric, WarningPanel, getToneForBucket, getToneForReviewEntry, getToneForReviewExplorer } from './triage-components.js';
import type { FilterRef } from './triage-components.js';

export function DashboardView(props: {
  status: DashboardStatus | null;
  route: RouteState;
  runDetail: RunDetailResponse | null;
  entryResponse: EntryListResponse | null;
  selectedEntry: EntryRecord | null;
  runBuckets: Array<{ bucket: string; count: number }>;
  filterInputRef: FilterRef;
  onSelectRun: (runId: string | null) => void;
  onSelectBucket: (bucket: string) => void;
  onFilterChange: (filter: string) => void;
  onSelectEntry: (entry: EntryRecord) => void;
  onChangeDiffMode: (mode: 'changed' | 'all') => void;
  onChangeReviewExplorer: (mode: 'all' | 'changed' | 'created' | 'deleted') => void;
  onOpenWorker: (workerId: string) => void;
}) {
  const { status, route, runDetail, entryResponse, selectedEntry, runBuckets, filterInputRef } = props;
  const useReviewExplorerTones = runDetail?.run.mode === 'Review';
  const runId = runDetail?.run.runId ?? null;
  const runTitle = runId ? formatRunIdDisplay(runId) : 'Select a run';
  return (
    <main className="dashboard-layout">
      <DashboardOverviewPanel status={status} />

      <section className="main-grid">
        <section className="card runs-card">
          <div className="card-header">
            <div>
              <p className="section-kicker">Run History</p>
              <h2>Recent runs</h2>
              <p className="runs-hint">Scroll for older runs</p>
            </div>
            <CopyLinkButton label="Copy run view link" />
          </div>
          <div className="runs-table">
            {status?.recentRuns.map((run) => (
              <button
                key={run.runId ?? run.path ?? 'run'}
                className={`run-row ${route.runId === run.runId ? 'selected' : ''}`}
                onClick={() => props.onSelectRun(run.runId)}
                type="button"
              >
                <strong>{run.status ?? 'Unknown'}</strong>
                <span>{run.mode ?? '-'}</span>
                <span>{run.artifactType}</span>
                <span className="run-row-time">
                  <AbsoluteTimeLabel timestamp={run.startedAt} />
                  <RelativeTimeLabel timestamp={run.startedAt} className="run-row-relative" />
                </span>
                <span>C {run.creates} / U {run.updates} / MR {run.manualReview}</span>
              </button>
            ))}
          </div>
        </section>

        <section className="card detail-card">
          <div className="card-header">
            <div className="detail-heading">
              <p className="section-kicker">Run Detail</p>
              <h2 className="detail-run-id" title={runId ?? undefined}>{runTitle}</h2>
            </div>
            <div className="header-actions">
              {runDetail?.run ? <span className="badge ghost">{runDetail.run.artifactType}</span> : null}
              <CopyLinkButton label="Copy detail link" />
            </div>
          </div>

          {runDetail?.warnings?.length ? <WarningPanel title="Run warnings" warnings={runDetail.warnings} /> : null}

          {runDetail?.run ? (
            <>
              <RunTimeline startedAt={runDetail.run.startedAt} completedAt={runDetail.run.completedAt ?? null} />

              <div className="detail-summary">
                <SummaryMetric label="Mode" value={runDetail.run.mode ?? '-'} />
                <SummaryMetric label="Status" value={runDetail.run.status ?? '-'} />
                <SummaryMetric label="Duration" value={runDetail.run.durationSeconds?.toString() ?? '-'} />
                <SummaryMetric label="Manual review" value={runDetail.run.manualReview.toString()} />
              </div>

              {runDetail.run.mode === 'Review' ? (
                <section className="review-strip">
                  <div className="review-tabs">
                    {(['all', 'changed', 'created', 'deleted'] as const).map((mode) => (
                      <button
                        key={mode}
                        type="button"
                        className={route.reviewExplorer === mode ? 'active' : ''}
                        data-tone={getToneForReviewExplorer(mode)}
                        onClick={() => props.onChangeReviewExplorer(mode)}
                      >
                        {mode} ({mode === 'all' ? runDetail.reviewExplorer.changed + runDetail.reviewExplorer.created + runDetail.reviewExplorer.deleted : runDetail.reviewExplorer[mode]})
                      </button>
                    ))}
                  </div>
                  <p>Review explorer prioritizes created, changed, and deleted-style triage instead of raw bucket order.</p>
                </section>
              ) : null}

              <div className="toolbar">
                <div className="bucket-tabs">
                  {runBuckets.map(({ bucket, count }) => (
                    <button
                      key={bucket}
                      type="button"
                      className={route.bucket === bucket ? 'active' : ''}
                      data-tone={getToneForBucket(bucket)}
                      onClick={() => props.onSelectBucket(bucket)}
                    >
                      {bucket} ({count})
                    </button>
                  ))}
                </div>
                <input
                  ref={filterInputRef}
                  aria-label="Filter entries"
                  placeholder="Filter by worker, reason, or category"
                  value={route.filter}
                  onChange={(event) => props.onFilterChange(event.target.value)}
                />
              </div>

              <div className="detail-content">
                <div className="entry-list">
                  {(entryResponse?.entries ?? []).map((entry) => (
                    <button
                      key={entry.entryId}
                      type="button"
                      className={`entry-row ${route.entryId === entry.entryId ? 'selected' : ''}`}
                      data-tone={useReviewExplorerTones ? getToneForReviewEntry(entry) : getToneForBucket(entry.bucket)}
                      onClick={() => props.onSelectEntry(entry)}
                    >
                      <strong>{entry.workerId ?? 'Unknown worker'}</strong>
                      <span>{entry.bucketLabel}</span>
                      <span>{entry.reason ?? entry.reviewCategory ?? 'No reason provided'}</span>
                      <span>{entry.staleDays !== null ? `${entry.staleDays}d stale` : 'fresh'}</span>
                    </button>
                  ))}
                </div>

                <SelectedEntryPanel
                  entry={selectedEntry}
                  diffMode={route.diffMode}
                  tone={selectedEntry && useReviewExplorerTones ? getToneForReviewEntry(selectedEntry) : undefined}
                  onDiffModeChange={props.onChangeDiffMode}
                  onOpenWorker={props.onOpenWorker}
                />
              </div>
            </>
          ) : (
            <p className="empty-state">Choose a run to inspect report buckets and selected-object details.</p>
          )}
        </section>
      </section>
    </main>
  );
}

function formatRunIdDisplay(runId: string): string {
  if (runId.length <= 18) {
    return runId;
  }

  return `${runId.slice(0, 8)}...${runId.slice(-8)}`;
}

export function QueueView(props: {
  route: RouteState;
  queueResponse: QueueResponse | null;
  filterInputRef: FilterRef;
  onQueueChange: (queue: QueueName) => void;
  onFilterChange: (filter: string) => void;
  onReasonChange: (reason: string) => void;
  onReviewCaseChange: (reviewCaseType: string) => void;
  onPageChange: (page: number) => void;
  onPageSizeChange: (pageSize: number) => void;
  onOpenWorker: (workerId: string) => void;
  onOpenRun: (entry: EntryRecord) => void;
}) {
  const { route, queueResponse, filterInputRef } = props;
  const start = queueResponse ? Math.min((queueResponse.page - 1) * queueResponse.pageSize + 1, queueResponse.total) : 0;
  const end = queueResponse ? Math.min(queueResponse.page * queueResponse.pageSize, queueResponse.total) : 0;
  const totalPages = queueResponse ? Math.max(Math.ceil(queueResponse.total / queueResponse.pageSize), 1) : 1;

  return (
    <main className="queue-grid">
      <section className="card queue-sidebar">
        <div className="card-header">
          <div>
            <p className="section-kicker">Queues</p>
            <h2>Exception triage</h2>
          </div>
          <CopyLinkButton label="Copy queue link" />
        </div>
        <div className="queue-tabs">
          {(['manual-review', 'quarantined', 'conflicts', 'guardrails'] as QueueName[]).map((queueName) => (
            <button key={queueName} type="button" className={route.queueName === queueName ? 'active' : ''} data-tone={getToneForBucket(queueName)} onClick={() => props.onQueueChange(queueName)}>
              {queueName}
            </button>
          ))}
        </div>
        <input
          ref={filterInputRef}
          aria-label="Queue filter"
          placeholder="Filter queue"
          value={route.filter}
          onChange={(event) => props.onFilterChange(event.target.value)}
        />
        <div className="queue-page-size">
          <label htmlFor="queue-page-size">Page size</label>
          <select
            id="queue-page-size"
            aria-label="Queue page size"
            value={route.pageSize}
            onChange={(event) => props.onPageSizeChange(Number(event.target.value))}
          >
            {[10, 25, 50].map((size) => (
              <option key={size} value={size}>{size}</option>
            ))}
          </select>
        </div>
        <GroupPanel title="Reasons" groups={queueResponse?.reasonGroups ?? []} activeKey={route.reason} onSelect={props.onReasonChange} />
        <GroupPanel title="Review cases" groups={queueResponse?.reviewCaseGroups ?? []} activeKey={route.reviewCaseType} onSelect={props.onReviewCaseChange} />
        <GroupPanel title="Artifacts" groups={queueResponse?.artifactGroups ?? []} activeKey="" onSelect={() => undefined} disabled />
      </section>

      <section className="card queue-results">
        <div className="card-header">
          <div>
            <p className="section-kicker">Queue Results</p>
            <h2>{queueResponse?.total ?? 0} matching entries</h2>
            <p className="queue-count-copy">
              {queueResponse?.total ? `Showing ${start}-${end} of ${queueResponse.total}` : 'No entries matched this queue view.'}
            </p>
          </div>
          <QueuePagination
            page={route.page}
            pageSize={route.pageSize}
            totalPages={totalPages}
            disabled={!queueResponse || queueResponse.total === 0}
            onPageChange={props.onPageChange}
          />
        </div>
        {queueResponse?.warnings?.length ? <WarningPanel title="Queue warnings" warnings={queueResponse.warnings} /> : null}
        <div className="queue-list">
          {(queueResponse?.entries ?? []).map((entry) => (
            <div className="queue-item" data-tone={getToneForBucket(entry.bucket)} key={entry.entryId}>
              <div>
                <strong>{entry.workerId ?? 'Unknown worker'}</strong>
                <p>{entry.reason ?? entry.reviewCaseType ?? entry.bucketLabel}</p>
                <small>{entry.artifactType} · {entry.bucketLabel} · {entry.staleDays !== null ? `${entry.staleDays}d stale` : 'fresh'}</small>
              </div>
              <div className="queue-item-actions">
                {entry.workerId ? <button type="button" onClick={() => props.onOpenWorker(entry.workerId!)}>Worker</button> : null}
                <button type="button" onClick={() => props.onOpenRun(entry)}>Run</button>
              </div>
            </div>
          ))}
        </div>
      </section>
    </main>
  );
}

export function WorkerView(props: {
  route: RouteState;
  workerDetail: WorkerDetailResponse | null;
  workerActionState: { pendingAction: WorkerActionKind | null; result: WorkerActionResponse | null };
  onRunWorkerAction: (action: WorkerActionKind) => void;
  onOpenRun: (runId: string | null, entry?: EntryRecord | null) => void;
}) {
  const { route, workerDetail, workerActionState } = props;
  return (
    <main className="worker-grid">
      <section className="card worker-summary">
        <div className="card-header">
          <div>
            <p className="section-kicker">Worker</p>
            <h2>{route.workerId ?? 'No worker selected'}</h2>
          </div>
          <CopyLinkButton label="Copy worker link" />
        </div>
        {workerDetail?.trackedWorker ? (
          <dl className="detail-list">
            <DetailRow label="Suppressed" value={String(Boolean(workerDetail.trackedWorker.suppressed))} />
            <DetailRow label="DN" value={workerDetail.trackedWorker.distinguishedName ?? '-'} />
            <DetailRow label="Delete after" value={workerDetail.trackedWorker.deleteAfter ?? '-'} />
            <DetailRow label="Last seen status" value={workerDetail.trackedWorker.lastSeenStatus ?? '-'} />
          </dl>
        ) : (
          <p className="empty-state">No tracked state is available for this worker.</p>
        )}
      </section>

      <section className="card worker-actions-card">
        <div className="card-header">
          <div>
            <p className="section-kicker">Scoped Actions</p>
            <h2>Single-worker sync</h2>
          </div>
        </div>
        <div className="worker-action-buttons">
          {([
            { action: 'test-sync', label: 'Test sync', detail: 'Minimal preview using previewQuery.' },
            { action: 'review-sync', label: 'Review sync', detail: 'Full preview using the main worker query.' },
            { action: 'real-sync', label: 'Real sync', detail: 'Apply the scoped worker sync to AD.' },
          ] as const).map(({ action, label, detail }) => (
            <button
              key={action}
              aria-label={label}
              type="button"
              onClick={() => props.onRunWorkerAction(action)}
              disabled={!route.workerId || workerActionState.pendingAction !== null}
            >
              <strong>{label}</strong>
              <span>{workerActionState.pendingAction === action ? 'Running...' : detail}</span>
            </button>
          ))}
        </div>
        {workerActionState.result ? (
          <div className="worker-action-result">
            <p>
              <strong>{formatWorkerActionLabel(workerActionState.result.action)}</strong> finished with{' '}
              <strong>{workerActionState.result.result.status ?? 'unknown status'}</strong>.
            </p>
            <p>
              {workerActionState.result.result.artifactType ?? 'Run'} · {workerActionState.result.result.runId ?? 'no run id'}
            </p>
            {workerActionState.result.result.previewMode ? (
              <p>Preview mode: {workerActionState.result.result.previewMode}</p>
            ) : null}
            <div className="queue-item-actions">
              {workerActionState.result.result.runId ? (
                <button
                  type="button"
                  onClick={() => {
                    const matchedEntry = workerDetail?.relatedEntries.find((entry) => entry.runId === workerActionState.result?.result.runId);
                    props.onOpenRun(workerActionState.result?.result.runId ?? null, matchedEntry ?? null);
                  }}
                >
                  Open run
                </button>
              ) : null}
            </div>
          </div>
        ) : (
          <p className="empty-state">Run a minimal preview, a full review preview, or the scoped real sync for this worker.</p>
        )}
      </section>

      <section className="card worker-latest">
        <div className="card-header">
          <div>
            <p className="section-kicker">Latest Selected Detail</p>
            <h2>{workerDetail?.latestEntry?.bucketLabel ?? 'No related entries'}</h2>
          </div>
        </div>
        <SelectedEntryPanel entry={workerDetail?.latestEntry ?? null} diffMode="changed" onDiffModeChange={() => undefined} onOpenWorker={() => undefined} compact />
      </section>

      <section className="card worker-history-card">
        <div className="card-header">
          <div>
            <p className="section-kicker">Related Runs</p>
            <h2>{workerDetail?.relatedRuns.length ?? 0} runs</h2>
          </div>
        </div>
        <div className="queue-list">
          {(workerDetail?.relatedEntries ?? []).map((entry) => (
            <div className="queue-item" data-tone={getToneForBucket(entry.bucket)} key={entry.entryId}>
              <div>
                <strong>{entry.bucketLabel}</strong>
                <p>{entry.reason ?? entry.reviewCategory ?? entry.groupLabel}</p>
                <small>
                  {entry.runId ?? '-'} · <AbsoluteTimeLabel timestamp={entry.startedAt} /> · <RelativeTimeLabel timestamp={entry.startedAt} /> · {entry.changeCount} changes
                </small>
              </div>
              <div className="queue-item-actions">
                <button type="button" onClick={() => props.onOpenRun(entry.runId, entry)}>Open run</button>
              </div>
            </div>
          ))}
        </div>
        {workerDetail?.warnings?.length ? <WarningPanel title="Worker warnings" warnings={workerDetail.warnings} /> : null}
      </section>
    </main>
  );
}

export function ReportExplorerView(props: {
  route: RouteState;
  runDetail: RunDetailResponse | null;
  entryResponse: EntryListResponse | null;
  selectedEntry: EntryRecord | null;
  onCategoryChange: (category: 'Changed' | 'Created' | 'Deleted') => void;
  onSelectEntry: (entry: EntryRecord) => void;
  onDiffModeChange: (mode: 'changed' | 'all') => void;
  onOpenWorker: (workerId: string) => void;
  onOpenPath: () => void;
  onCopyPath: () => void;
  onExport: () => void;
}) {
  const entries = props.entryResponse?.entries ?? [];
  const categoryEntries = entries.filter((entry) => mapEntryCategory(entry) === props.route.reportCategory);
  return (
    <main className="dashboard-layout">
      <section className="card detail-card full-width">
        <div className="card-header">
          <div className="detail-heading">
            <p className="section-kicker">Report Explorer</p>
            <h2 className="detail-run-id" title={props.runDetail?.run.runId ?? undefined}>{props.runDetail?.run.runId ?? 'Select a run'}</h2>
          </div>
          <div className="header-actions">
            <button type="button" onClick={props.onOpenPath}>Open path</button>
            <button type="button" onClick={props.onCopyPath}>Copy path</button>
            <button type="button" onClick={props.onExport}>Export bucket</button>
          </div>
        </div>
        <div className="review-tabs">
          {(['Changed', 'Created', 'Deleted'] as const).map((category) => (
            <button
              key={category}
              type="button"
              className={props.route.reportCategory === category ? 'active' : ''}
              onClick={() => props.onCategoryChange(category)}
            >
              {category} ({entries.filter((entry) => mapEntryCategory(entry) === category).length})
            </button>
          ))}
        </div>
        <div className="detail-content">
          <div className="entry-list">
            {categoryEntries.map((entry) => (
              <button
                key={entry.entryId}
                type="button"
                className={`entry-row ${props.route.entryId === entry.entryId ? 'selected' : ''}`}
                data-tone={getToneForBucket(entry.bucket)}
                onClick={() => props.onSelectEntry(entry)}
              >
                <strong>{entry.workerId ?? 'Unknown worker'}</strong>
                <span>{entry.bucketLabel}</span>
                <span>{entry.reason ?? entry.reviewCategory ?? 'No reason provided'}</span>
                <span>{entry.changeCount} changes</span>
              </button>
            ))}
          </div>
          <SelectedEntryPanel
            entry={props.selectedEntry}
            diffMode={props.route.diffMode}
            onDiffModeChange={props.onDiffModeChange}
            onOpenWorker={props.onOpenWorker}
          />
        </div>
      </section>
    </main>
  );
}

export function OperationsView(props: {
  onRunAction: (action: OperatorActionKind) => void;
  onRunPreflight: () => void;
  onRunFreshReset: () => void;
}) {
  const runActions: Array<{ action: OperatorActionKind; label: string; detail: string }> = [
    { action: 'delta-dry-run', label: 'Delta dry-run', detail: 'Write runtime status and report files only.' },
    { action: 'delta-sync', label: 'Delta sync', detail: 'Apply delta changes to AD and sync state.' },
    { action: 'full-dry-run', label: 'Full dry-run', detail: 'Generate a full report without AD mutations.' },
    { action: 'full-sync', label: 'Full sync', detail: 'Apply a full synchronization to AD.' },
    { action: 'review-run', label: 'First-sync review', detail: 'Launch the review flow in a separate PowerShell process.' },
  ];

  return (
    <main className="worker-grid">
      <section className="card worker-actions-card">
        <div className="card-header">
          <div>
            <p className="section-kicker">Operations</p>
            <h2>Command launcher</h2>
          </div>
        </div>
        <div className="worker-action-buttons">
          <button type="button" onClick={props.onRunPreflight}>
            <strong>Preflight</strong>
            <span>Run validation against the current config and mapping files.</span>
          </button>
          {runActions.map(({ action, label, detail }) => (
            <button key={action} type="button" onClick={() => props.onRunAction(action)}>
              <strong>{label}</strong>
              <span>{detail}</span>
            </button>
          ))}
          <button type="button" onClick={props.onRunFreshReset}>
            <strong>Fresh sync reset</strong>
            <span>Delete managed AD user objects and reset local sync state.</span>
          </button>
        </div>
      </section>
    </main>
  );
}

function formatWorkerActionLabel(action: WorkerActionKind): string {
  switch (action) {
    case 'test-sync':
      return 'Test sync';
    case 'review-sync':
      return 'Review sync';
    case 'real-sync':
      return 'Real sync';
  }
}

function mapEntryCategory(entry: EntryRecord): 'Changed' | 'Created' | 'Deleted' {
  if (entry.bucket === 'creates' || entry.reviewCategory === 'NewUser') {
    return 'Created';
  }
  if (['updates', 'enables', 'disables', 'graveyardMoves', 'unchanged'].includes(entry.bucket)) {
    return 'Changed';
  }
  return 'Deleted';
}

function QueuePagination(props: {
  page: number;
  pageSize: number;
  totalPages: number;
  disabled: boolean;
  onPageChange: (page: number) => void;
}) {
  return (
    <div className="pagination-controls">
      <button type="button" onClick={() => props.onPageChange(props.page - 1)} disabled={props.disabled || props.page <= 1}>Previous</button>
      <span>Page {props.page} of {props.totalPages}</span>
      <button type="button" onClick={() => props.onPageChange(props.page + 1)} disabled={props.disabled || props.page >= props.totalPages}>Next</button>
    </div>
  );
}

function RunTimeline(props: { startedAt: string | null; completedAt: string | null }) {
  const { startedAt, completedAt } = props;
  const elapsed = formatElapsed(startedAt, completedAt);

  return (
    <section className="run-timeline" aria-label="Run timeline">
      <div className="run-timeline-item run-timeline-combined">
        <div>
          <span className="run-timeline-label">Started</span>
          <strong><AbsoluteTimeLabel timestamp={startedAt} /></strong>
          <RelativeTimeLabel timestamp={startedAt} className="run-timeline-relative" />
        </div>
        <div>
          <span className="run-timeline-label">Completed</span>
          <strong><AbsoluteTimeLabel timestamp={completedAt} emptyLabel="In progress" /></strong>
          <RelativeTimeLabel timestamp={completedAt} emptyLabel="In progress" className="run-timeline-relative" />
        </div>
        <div>
          <span className="run-timeline-label">Elapsed</span>
          <strong>{elapsed}</strong>
          <span className="run-timeline-relative">{completedAt ? 'total run time' : 'still running'}</span>
        </div>
      </div>
    </section>
  );
}

function formatElapsed(startedAt: string | null, completedAt: string | null): string {
  if (!startedAt) {
    return '-';
  }

  const start = new Date(startedAt).getTime();
  const end = completedAt ? new Date(completedAt).getTime() : Date.now();
  if (Number.isNaN(start) || Number.isNaN(end)) {
    return '-';
  }

  const totalSeconds = Math.max(0, Math.floor((end - start) / 1000));
  const days = Math.floor(totalSeconds / 86400);
  const hours = Math.floor((totalSeconds % 86400) / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  const parts = [
    days ? `${days}d` : null,
    hours ? `${hours}h` : null,
    minutes ? `${minutes}m` : null,
    `${seconds}s`,
  ].filter(Boolean);

  return parts.join(' ');
}
