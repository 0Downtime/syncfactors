import type { DashboardStatus, EntryListResponse, EntryRecord, OperatorActionKind, OperatorCommandResult, QueueName, QueueResponse, RunDetailResponse, WorkerActionKind, WorkerActionResponse, WorkerDetailResponse } from './types.js';
import type { RouteState } from './route-state.js';
import { mapEntryToReportCategory, mapReviewExplorerToBucket } from './route-state.js';
import { AbsoluteTimeLabel, CopyLinkButton, DashboardOverviewPanel, DetailRow, GroupPanel, RelativeTimeLabel, SelectedEntryPanel, SummaryMetric, TriageGuidancePanel, WarningPanel, getToneForBucket, getToneForReviewEntry, getToneForReviewExplorer } from './triage-components.js';
import type { FilterRef } from './triage-components.js';
import type { RunSummary } from './types.js';

function renderRunCount(label: string, value: number, tone?: string) {
  return (
    <span className="run-row-count" data-tone={value > 0 ? tone : undefined}>
      {label} {value}
    </span>
  );
}

function RunRowCounts(props: { run: RunSummary }) {
  const { run } = props;
  return (
    <span className="run-row-counts">
      {renderRunCount('C', run.creates, getToneForBucket('creates'))}
      <span className="run-row-count-separator">/</span>
      {renderRunCount('U', run.updates, getToneForBucket('updates'))}
      <span className="run-row-count-separator">/</span>
      {renderRunCount('D', run.disables, 'update')}
      <span className="run-row-count-separator">/</span>
      {renderRunCount('X', run.deletions, getToneForBucket('deletions'))}
      <span className="run-row-count-separator">/</span>
      {renderRunCount('Q', run.quarantined, getToneForBucket('quarantined'))}
      <span className="run-row-count-separator">/</span>
      {renderRunCount('F', run.conflicts, getToneForBucket('conflicts'))}
      <span className="run-row-count-separator">/</span>
      {renderRunCount('GF', run.guardrailFailures, getToneForBucket('guardrailFailures'))}
      <span className="run-row-count-separator">/</span>
      {renderRunCount('MR', run.manualReview, getToneForBucket('manualReview'))}
    </span>
  );
}

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
  onOpenReport: () => void;
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
                <RunRowCounts run={run} />
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
          {runDetail?.run?.errorMessage ? <WarningPanel title="Run failure" warnings={[runDetail.run.errorMessage]} /> : null}

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

                <TriageGuidancePanel
                  entry={selectedEntry}
                  onOpenWorker={props.onOpenWorker}
                  onOpenReport={props.onOpenReport}
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
  const queueInsights = getQueueInsights(queueResponse);

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
        <section className="queue-workbench" aria-label="Queue workbench">
          <div className="queue-workbench-copy">
            <p className="section-kicker">Queue Workbench</p>
            <h3>{queueInsights.headline}</h3>
            <p>{queueInsights.summary}</p>
          </div>
          <div className="detail-summary queue-workbench-metrics">
            <SummaryMetric label="Dominant reason" value={queueInsights.dominantReason} />
            <SummaryMetric label="Stalest item" value={queueInsights.stalestLabel} />
            <SummaryMetric label="Newest item" value={queueInsights.newestLabel} />
            <SummaryMetric label="Next case" value={queueInsights.nextCaseLabel} />
          </div>
          <div className="queue-workbench-actions">
            {queueInsights.nextEntry?.workerId ? (
              <button type="button" onClick={() => props.onOpenWorker(queueInsights.nextEntry!.workerId!)}>
                Open recommended worker
              </button>
            ) : null}
            {queueInsights.nextEntry ? (
              <button type="button" onClick={() => props.onOpenRun(queueInsights.nextEntry!)}>
                Open recommended run
              </button>
            ) : null}
          </div>
        </section>
        <div className="queue-list">
          {(queueResponse?.entries ?? []).map((entry) => (
            <div className="queue-item" data-tone={getToneForBucket(entry.bucket)} key={entry.entryId}>
              <div>
                <strong>{entry.workerId ?? 'Unknown worker'}</strong>
                <p>{entry.reason ?? entry.reviewCaseType ?? entry.bucketLabel}</p>
                <small>{entry.artifactType} · {entry.bucketLabel} · {entry.staleDays !== null ? `${entry.staleDays}d stale` : 'fresh'}</small>
              </div>
              <div className="queue-item-actions">
                {entry.workerId ? <button type="button" onClick={() => props.onOpenWorker(entry.workerId!)}>Worker detail</button> : null}
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
  const workerInsights = getWorkerInsights(workerDetail);
  return (
    <main className="worker-grid">
      <section className="card worker-summary">
        <div className="card-header">
          <div>
            <p className="section-kicker">Worker Detail</p>
            <h2>{route.workerId ?? 'No worker selected'}</h2>
          </div>
          <CopyLinkButton label="Copy worker detail link" />
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
        <section className="worker-significance">
          <p className="section-kicker">Current Significance</p>
          <h3>{workerInsights.headline}</h3>
          <p>{workerInsights.summary}</p>
          <dl className="detail-list">
            <DetailRow label="Current queue" value={workerInsights.currentQueue} />
            <DetailRow label="Review case" value={workerInsights.reviewCase} />
            <DetailRow label="Deletion risk" value={workerInsights.deletionRisk} />
            <DetailRow label="Latest action" value={workerInsights.latestAction} />
          </dl>
        </section>
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
            <p className="section-kicker">Latest Related Entry</p>
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
  filterInputRef: FilterRef;
  onCategoryChange: (category: 'Changed' | 'Created' | 'Deleted') => void;
  onSelectEntry: (entry: EntryRecord) => void;
  onDiffModeChange: (mode: 'changed' | 'all') => void;
  onOpenWorker: (workerId: string) => void;
  onOpenPath: () => void;
  onCopyPath: () => void;
  onExport: () => void;
  onFilterChange: (filter: string) => void;
}) {
  const entries = props.entryResponse?.entries ?? [];
  const categoryEntries = entries.filter((entry) => mapEntryToReportCategory(entry) === props.route.reportCategory);
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
        <section className="report-identity-strip">
          <div className="report-identity-copy">
            <p className="section-kicker">Artifact Inspection</p>
            <p>Report Explorer is for deep structured diffs, category traversal, exports, and report path actions.</p>
          </div>
          <div className="detail-summary report-summary">
            <SummaryMetric label="Mode" value={props.runDetail?.run.mode ?? '-'} />
            <SummaryMetric label="Status" value={props.runDetail?.run.status ?? '-'} />
            <SummaryMetric label="Entries" value={String(entries.length)} />
            <SummaryMetric label="Warnings" value={String(props.runDetail?.warnings.length ?? 0)} />
          </div>
        </section>
        <div className="review-tabs">
          {(['Changed', 'Created', 'Deleted'] as const).map((category) => (
            <button
              key={category}
              type="button"
              className={props.route.reportCategory === category ? 'active' : ''}
              onClick={() => props.onCategoryChange(category)}
            >
              {category} ({entries.filter((entry) => mapEntryToReportCategory(entry) === category).length})
            </button>
          ))}
        </div>
        <div className="toolbar">
          <input
            ref={props.filterInputRef}
            aria-label="Report filter"
            placeholder="Filter by worker, reason, or review category"
            value={props.route.filter}
            onChange={(event) => props.onFilterChange(event.target.value)}
          />
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
  status: DashboardStatus | null;
  pendingActionLabel: string | null;
  latestResult: OperatorCommandResult | null;
  recentResults: OperatorCommandResult[];
  streamConnected: boolean;
  workerLauncherId: string;
  workerLauncherMode: 'minimal' | 'full';
  onRunAction: (action: OperatorActionKind) => void;
  onRunPreflight: () => void;
  onRunFreshReset: () => void;
  onOpenLatestRun: (runId: string | null) => void;
  onWorkerLauncherIdChange: (workerId: string) => void;
  onWorkerLauncherModeChange: (mode: 'minimal' | 'full') => void;
  onPreviewWorker: () => void;
  onOpenWorker: () => void;
}) {
  const currentRunActive = `${props.status?.currentRun?.status ?? ''}` === 'InProgress';
  const hasRecentRunContext = Boolean(props.status?.recentRuns?.some((run) => run.mappingConfigPath));
  const runActions: Array<{ action: OperatorActionKind; label: string; detail: string }> = [
    { action: 'delta-dry-run', label: 'Delta dry-run', detail: 'Write runtime status and report files only.' },
    { action: 'delta-sync', label: 'Delta sync', detail: 'Apply delta changes to AD and sync state.' },
    { action: 'full-dry-run', label: 'Full dry-run', detail: 'Generate a full report without AD mutations.' },
    { action: 'full-sync', label: 'Full sync', detail: 'Apply a full synchronization to AD.' },
    { action: 'review-run', label: 'First-sync review', detail: 'Launch the review flow in a separate PowerShell process.' },
  ];
  const disabledReason = !hasRecentRunContext
    ? 'Run at least one sync/review with mapping metadata before launching browser actions.'
    : currentRunActive
      ? 'A sync is already in progress. Wait for it to complete before launching another action.'
      : null;

  return (
    <main className="worker-grid">
      <section className="card worker-summary">
        <div className="card-header">
          <div>
            <p className="section-kicker">Operator State</p>
            <h2>{currentRunActive ? 'Run in progress' : 'Ready'}</h2>
          </div>
          <span className="badge ghost">{props.streamConnected ? 'Live via SSE' : 'Polling fallback'}</span>
        </div>
        <dl className="detail-list">
          <DetailRow label="Current run" value={`${props.status?.currentRun?.status ?? 'Idle'} / ${props.status?.currentRun?.stage ?? 'Completed'}`} />
          <DetailRow label="Current worker" value={`${props.status?.currentRun?.currentWorkerId ?? '-'}`} />
          <DetailRow label="Mapping metadata" value={hasRecentRunContext ? 'Available' : 'Missing'} />
          <DetailRow label="Pending command" value={props.pendingActionLabel ?? '-'} />
        </dl>
        {disabledReason ? <WarningPanel title="Action gating" warnings={[disabledReason]} /> : null}
      </section>

      <section className="card worker-actions-card">
        <div className="card-header">
          <div>
            <p className="section-kicker">Worker Launchpad</p>
            <h2>Scoped preview from operations</h2>
          </div>
        </div>
        <div className="operation-launchpad">
          <label className="operation-launchpad-field">
            <span>Worker ID</span>
            <input
              aria-label="Operation worker id"
              placeholder={`${props.status?.currentRun?.currentWorkerId ?? props.status?.recentRuns?.[0]?.workerScope?.workerId ?? 'Enter worker id'}`}
              value={props.workerLauncherId}
              onChange={(event) => props.onWorkerLauncherIdChange(event.target.value)}
            />
          </label>
          <div className="operation-launchpad-mode">
            <span>Preview mode</span>
            <div className="toggle-row">
              <button type="button" className={props.workerLauncherMode === 'minimal' ? 'active' : ''} onClick={() => props.onWorkerLauncherModeChange('minimal')}>
                Minimal
              </button>
              <button type="button" className={props.workerLauncherMode === 'full' ? 'active' : ''} onClick={() => props.onWorkerLauncherModeChange('full')}>
                Full
              </button>
            </div>
          </div>
          <div className="queue-item-actions">
            <button type="button" onClick={props.onPreviewWorker} disabled={Boolean(disabledReason) || !props.workerLauncherId.trim()}>
              Preview worker
            </button>
            <button type="button" onClick={props.onOpenWorker} disabled={!props.workerLauncherId.trim()}>
              Open worker detail
            </button>
          </div>
        </div>
      </section>

      <section className="card worker-actions-card">
        <div className="card-header">
          <div>
            <p className="section-kicker">Operations</p>
            <h2>Safe and scoped actions</h2>
          </div>
        </div>
        <div className="worker-action-buttons">
          <button type="button" onClick={props.onRunPreflight} disabled={currentRunActive || !hasRecentRunContext}>
            <strong>Preflight</strong>
            <span>Run validation against the current config and mapping files.</span>
          </button>
          {runActions.slice(0, 3).map(({ action, label, detail }) => (
            <button key={action} type="button" onClick={() => props.onRunAction(action)} disabled={Boolean(disabledReason)}>
              <strong>{label}</strong>
              <span>{detail}</span>
            </button>
          ))}
        </div>
      </section>

      <section className="card worker-actions-card" data-tone="delete">
        <div className="card-header">
          <div>
            <p className="section-kicker">Write Actions</p>
            <h2>Broad mutations</h2>
          </div>
        </div>
        <div className="worker-action-buttons">
          {runActions.slice(3).map(({ action, label, detail }) => (
            <button key={action} type="button" onClick={() => props.onRunAction(action)} disabled={Boolean(disabledReason)}>
              <strong>{label}</strong>
              <span>{detail}</span>
            </button>
          ))}
          <button type="button" onClick={props.onRunFreshReset} disabled={Boolean(disabledReason)}>
            <strong>Fresh sync reset</strong>
            <span>Delete managed AD user objects and reset local sync state.</span>
          </button>
        </div>
      </section>

      <section className="card worker-latest">
        <div className="card-header">
          <div>
            <p className="section-kicker">Latest Result</p>
            <h2>{props.latestResult?.message ?? 'No commands run yet'}</h2>
          </div>
        </div>
        {props.latestResult ? (
          <div className="queue-list">
            <div className="queue-item">
              <div>
                <strong>{props.latestResult.completed ? 'Completed' : 'Accepted'}</strong>
                <p>{props.latestResult.commandSummary.join(' · ') || 'No command summary recorded.'}</p>
                <small>{props.latestResult.reportPath ?? props.latestResult.runId ?? '-'}</small>
              </div>
              <div className="queue-item-actions">
                {props.latestResult.runId ? <button type="button" onClick={() => props.onOpenLatestRun(props.latestResult.runId)}>Open run</button> : null}
              </div>
            </div>
          </div>
        ) : (
          <p className="empty-state">Run an operation to see the latest result and jump straight into the produced run.</p>
        )}
      </section>

      <section className="card worker-history-card">
        <div className="card-header">
          <div>
            <p className="section-kicker">Recent Commands</p>
            <h2>{props.recentResults.length} results</h2>
          </div>
        </div>
        <div className="queue-list">
          {props.recentResults.length === 0 ? (
            <p className="empty-state">No commands have completed in this session.</p>
          ) : props.recentResults.map((result, index) => (
            <div className="queue-item" key={`${result.message}:${result.runId ?? result.reportPath ?? index}`}>
              <div>
                <strong>{result.message}</strong>
                <p>{result.commandSummary.join(' · ') || 'No command summary recorded.'}</p>
                <small>{result.outputLines[0] ?? result.reportPath ?? result.runId ?? '-'}</small>
              </div>
              <div className="queue-item-actions">
                {result.runId ? <button type="button" onClick={() => props.onOpenLatestRun(result.runId)}>Open run</button> : null}
              </div>
            </div>
          ))}
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

function getQueueInsights(queueResponse: QueueResponse | null): {
  headline: string;
  summary: string;
  dominantReason: string;
  stalestLabel: string;
  newestLabel: string;
  nextCaseLabel: string;
  nextEntry: EntryRecord | null;
} {
  const entries = queueResponse?.entries ?? [];
  if (entries.length === 0) {
    return {
      headline: 'No matching queue items',
      summary: 'Adjust the current filters or switch queues to find the next case to work.',
      dominantReason: '-',
      stalestLabel: '-',
      newestLabel: '-',
      nextCaseLabel: '-',
      nextEntry: null,
    };
  }

  const dominantReason = getDominantLabel(entries.map((entry) => entry.reason ?? entry.reviewCaseType ?? entry.bucketLabel));
  const stalestEntry = [...entries].sort((left, right) => (right.staleDays ?? -1) - (left.staleDays ?? -1))[0] ?? null;
  const newestEntry = [...entries].sort((left, right) => {
    const leftTime = left.startedAt ? new Date(left.startedAt).getTime() : 0;
    const rightTime = right.startedAt ? new Date(right.startedAt).getTime() : 0;
    return rightTime - leftTime;
  })[0] ?? null;
  const nextEntry = [...entries].sort((left, right) => {
    const staleDelta = (right.staleDays ?? -1) - (left.staleDays ?? -1);
    if (staleDelta !== 0) {
      return staleDelta;
    }

    return right.changeCount - left.changeCount;
  })[0] ?? null;

  return {
    headline: `${formatQueueName(queueResponse?.queueName ?? null)} needs operator attention`,
    summary: nextEntry
      ? `Start with worker ${nextEntry.workerId ?? 'unknown'} because it is the most time-sensitive case visible in this queue view.`
      : 'Review the queue list to decide the next operator action.',
    dominantReason,
    stalestLabel: stalestEntry ? formatEntryAge(stalestEntry) : '-',
    newestLabel: newestEntry ? formatNewestEntry(newestEntry) : '-',
    nextCaseLabel: nextEntry ? `${nextEntry.workerId ?? 'Unknown'} (${nextEntry.reason ?? nextEntry.reviewCaseType ?? nextEntry.bucketLabel})` : '-',
    nextEntry,
  };
}

function getWorkerInsights(workerDetail: WorkerDetailResponse | null): {
  headline: string;
  summary: string;
  currentQueue: string;
  reviewCase: string;
  deletionRisk: string;
  latestAction: string;
} {
  const latestEntry = workerDetail?.latestEntry ?? workerDetail?.relatedEntries[0] ?? null;
  const trackedWorker = workerDetail?.trackedWorker ?? null;
  const deletionRisk = trackedWorker?.deleteAfter
    ? `Pending deletion after ${trackedWorker.deleteAfter}`
    : trackedWorker?.suppressed
      ? 'Suppressed but not on a deletion timer'
      : 'No active deletion risk recorded';
  const currentQueue = latestEntry?.queueName ?? latestEntry?.bucketLabel ?? 'No queue placement';
  const reviewCase = latestEntry?.reviewCaseType ?? latestEntry?.reviewCategory ?? latestEntry?.reason ?? '-';
  const latestAction = latestEntry?.operatorActionSummary ?? latestEntry?.operationSummary?.action ?? latestEntry?.reason ?? 'No recent action summary';

  if (!latestEntry && !trackedWorker) {
    return {
      headline: 'No current worker context',
      summary: 'Load a worker with related report history to see why it currently matters.',
      currentQueue,
      reviewCase,
      deletionRisk,
      latestAction,
    };
  }

  const headline = trackedWorker?.suppressed
    ? 'Suppressed worker still needs operator judgment'
    : latestEntry?.queueName
      ? 'Worker is still present in an exception flow'
      : 'Worker has recent report history';
  const summary = trackedWorker?.suppressed
    ? 'This worker is suppressed in tracked state, so the next decision is whether the current review result should keep, restore, or retire the identity.'
    : latestEntry?.reviewCaseType
      ? `The latest run still places this worker in ${latestEntry.reviewCaseType}, so the worker page should answer whether the case is ready for a targeted preview or sync.`
      : 'Use this page to understand the worker state before launching a scoped preview or sync.';

  return {
    headline,
    summary,
    currentQueue,
    reviewCase,
    deletionRisk,
    latestAction,
  };
}

function getDominantLabel(values: string[]): string {
  if (values.length === 0) {
    return '-';
  }

  const counts = new Map<string, number>();
  for (const value of values) {
    counts.set(value, (counts.get(value) ?? 0) + 1);
  }

  const [label] = [...counts.entries()].sort((left, right) => {
    if (right[1] !== left[1]) {
      return right[1] - left[1];
    }

    return left[0].localeCompare(right[0]);
  })[0] ?? ['-', 0];

  return label;
}

function formatQueueName(queueName: QueueName | null): string {
  if (!queueName) {
    return 'Queue';
  }

  return queueName
    .split('-')
    .map((segment) => segment.charAt(0).toUpperCase() + segment.slice(1))
    .join(' ');
}

function formatEntryAge(entry: EntryRecord): string {
  return `${entry.workerId ?? 'Unknown'} (${entry.staleDays !== null ? `${entry.staleDays}d stale` : 'fresh'})`;
}

function formatNewestEntry(entry: EntryRecord): string {
  return `${entry.workerId ?? 'Unknown'} (${entry.startedAt ? 'latest run' : 'undated'})`;
}
