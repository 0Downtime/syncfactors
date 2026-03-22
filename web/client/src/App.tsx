import { useEffect, useMemo, useRef, useState } from 'react';
import {
  applyWorker,
  copyRunReportPath,
  exportRunBucket,
  getQueue,
  getRun,
  getRunEntries,
  getStatus,
  getWorkerDetail,
  openLocalPath,
  openRunReport,
  previewWorker,
  runFreshReset,
  runOperatorAction,
  runPreflight,
  runWorkerAction,
} from './api.js';
import {
  BUCKET_ORDER,
  chooseSelectedEntry,
  getRouteState,
  mapEntryToReportCategory,
  mapReviewExplorerToBucket,
  normalizeRoute,
  resolveActiveBucket,
  stepSelection,
  syncRouteState,
} from './route-state.js';
import type { RouteState } from './route-state.js';
import {
  CommandResultPanel,
  ConfirmationDialog,
  StatusNote,
  WarningPanel,
  WorkerPreviewPanel,
} from './triage-components.js';
import { DashboardView, OperationsView, QueueView, ReportExplorerView, WorkerView } from './triage-views.js';
import type {
  ConfirmationDescriptor,
  DashboardStatus,
  EntryListResponse,
  OperatorActionKind,
  OperatorCommandResult,
  QueueResponse,
  RunDetailResponse,
  WorkerActionKind,
  WorkerActionResponse,
  WorkerDetailResponse,
  WorkerPreviewMode,
  WorkerPreviewResponse,
} from './types.js';

type ThemeMode = 'light' | 'dark';
type PendingConfirmation =
  | { kind: 'worker-apply'; descriptor: ConfirmationDescriptor; confirmText: string; workerId: string }
  | { kind: 'run-open'; descriptor: ConfirmationDescriptor; confirmText: string }
  | { kind: 'run-copy'; descriptor: ConfirmationDescriptor; confirmText: string }
  | { kind: 'run-export'; descriptor: ConfirmationDescriptor; confirmText: string }
  | { kind: 'fresh-reset'; descriptor: ConfirmationDescriptor; confirmText: string; countText: string; destructiveText: string };

const THEME_STORAGE_KEY = 'syncfactors-theme';
const NON_WINDOWS_AD_WARNING = 'Active Directory health probe is skipped on non-Windows hosts for the web dashboard.';

export function App() {
  const [status, setStatus] = useState<DashboardStatus | null>(null);
  const [route, setRoute] = useState<RouteState>(() => getRouteState());
  const [runDetail, setRunDetail] = useState<RunDetailResponse | null>(null);
  const [entryResponse, setEntryResponse] = useState<EntryListResponse | null>(null);
  const [queueResponse, setQueueResponse] = useState<QueueResponse | null>(null);
  const [workerDetail, setWorkerDetail] = useState<WorkerDetailResponse | null>(null);
  const [workerActionState, setWorkerActionState] = useState<{ pendingAction: WorkerActionKind | null; result: WorkerActionResponse | null }>({
    pendingAction: null,
    result: null,
  });
  const [workerPreview, setWorkerPreview] = useState<WorkerPreviewResponse | null>(null);
  const [pendingConfirmation, setPendingConfirmation] = useState<PendingConfirmation | null>(null);
  const [commandResult, setCommandResult] = useState<OperatorCommandResult | null>(null);
  const [recentCommandResults, setRecentCommandResults] = useState<OperatorCommandResult[]>([]);
  const [pendingOperatorActionLabel, setPendingOperatorActionLabel] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [streamConnected, setStreamConnected] = useState(false);
  const [operationsWorkerId, setOperationsWorkerId] = useState('');
  const [operationsPreviewMode, setOperationsPreviewMode] = useState<WorkerPreviewMode>('full');
  const [theme, setTheme] = useState<ThemeMode>(() => getInitialTheme());
  const filterInputRef = useRef<HTMLInputElement | null>(null);
  const reportMenuRef = useRef<HTMLDetailsElement | null>(null);
  const streamConnectedRef = useRef(false);

  useEffect(() => {
    streamConnectedRef.current = streamConnected;
  }, [streamConnected]);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    document.documentElement.style.colorScheme = theme;
    if (hasStorageAccess()) {
      window.localStorage.setItem(THEME_STORAGE_KEY, theme);
    }
  }, [theme]);

  useEffect(() => {
    const onPopState = () => setRoute(getRouteState());
    window.addEventListener('popstate', onPopState);
    return () => window.removeEventListener('popstate', onPopState);
  }, []);

  useEffect(() => {
    let cancelled = false;
    const applyStatus = (nextStatus: DashboardStatus) => {
      setStatus(nextStatus);
      setRoute((current) => {
        const nextRunId = current.runId ?? nextStatus.recentRuns[0]?.runId ?? null;
        const nextWorkerId = current.workerId ?? nextStatus.recentRuns[0]?.workerScope?.workerId ?? null;
        const next = normalizeRoute({ ...current, runId: nextRunId, workerId: nextWorkerId }, nextStatus);
        syncRouteState(next);
        return next;
      });
    };

    const loadStatus = async () => {
      try {
        const nextStatus = await getStatus();
        if (!cancelled) {
          applyStatus(nextStatus);
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load dashboard status.');
        }
      }
    };

    void loadStatus();

    let stream: EventSource | null = null;
    if (typeof window !== 'undefined' && 'EventSource' in window) {
      stream = new window.EventSource('/api/status/stream');
      stream.addEventListener('status', (event) => {
        const message = event as MessageEvent<string>;
        try {
          const payload = JSON.parse(message.data) as { status?: DashboardStatus };
          if (payload.status && !cancelled) {
            setStreamConnected(true);
            applyStatus(payload.status);
            setError((current) => current?.includes('Failed to load dashboard status.') ? null : current);
          }
        } catch {
          if (!cancelled) {
            setError('Failed to parse streamed dashboard status.');
          }
        }
      });
      stream.addEventListener('error', () => {
        if (!cancelled) {
          setStreamConnected(false);
        }
      });
    }

    const interval = window.setInterval(() => {
      if (!streamConnectedRef.current) {
        void loadStatus();
      }
    }, 10000);

    return () => {
      cancelled = true;
      window.clearInterval(interval);
      stream?.close();
    };
  }, []);

  useEffect(() => {
    if (!route.runId) {
      setRunDetail(null);
      setEntryResponse(null);
      return;
    }

    let cancelled = false;
    void (async () => {
      try {
        const [nextRunDetail, nextEntries] = await Promise.all([
          getRun(route.runId!),
          getRunEntries(route.runId!, {
            bucket: route.view === 'report' ? mapReportCategoryToBucket(route.reportCategory) : resolveActiveBucket(route.bucket, route.reviewExplorer),
            filter: route.filter || undefined,
          }),
        ]);
        if (cancelled) {
          return;
        }

        setRunDetail(nextRunDetail);
        setEntryResponse(nextEntries);
        const resolvedEntry = chooseSelectedEntry(getVisibleEntries(nextEntries.entries, route), route.entryId);
        if (resolvedEntry && resolvedEntry.entryId !== route.entryId) {
          setRouteAndUrl({ ...route, entryId: resolvedEntry.entryId, workerId: resolvedEntry.workerId ?? route.workerId }, false);
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load run detail.');
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [route.runId, route.bucket, route.filter, route.reviewExplorer, route.view, route.reportCategory]);

  useEffect(() => {
    if (route.view !== 'queues') {
      setQueueResponse(null);
      return;
    }

    let cancelled = false;
    void (async () => {
      try {
        const nextQueue = await getQueue(route.queueName, {
          reason: route.reason || undefined,
          reviewCaseType: route.reviewCaseType || undefined,
          workerId: route.workerId || undefined,
          filter: route.filter || undefined,
          page: route.page,
          pageSize: route.pageSize,
        });
        if (!cancelled) {
          setQueueResponse(nextQueue);
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load queue.');
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [route.view, route.queueName, route.reason, route.reviewCaseType, route.workerId, route.filter, route.page, route.pageSize]);

  useEffect(() => {
    if (route.view !== 'worker' || !route.workerId) {
      setWorkerDetail(null);
      return;
    }

    let cancelled = false;
    void (async () => {
      try {
        const nextWorker = await getWorkerDetail(route.workerId!);
        if (!cancelled) {
          setWorkerDetail(nextWorker);
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load worker detail.');
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [route.view, route.workerId]);

  useEffect(() => {
    if (route.view !== 'operations' || operationsWorkerId) {
      return;
    }

    const currentWorkerId =
      typeof status?.currentRun?.currentWorkerId === 'string' && status.currentRun.currentWorkerId
        ? status.currentRun.currentWorkerId
        : null;
    const suggestedWorkerId =
      route.workerId
      ?? currentWorkerId
      ?? status?.recentRuns?.[0]?.workerScope?.workerId
      ?? '';
    if (suggestedWorkerId) {
      setOperationsWorkerId(suggestedWorkerId);
    }
  }, [route.view, route.workerId, status, operationsWorkerId]);

  const visibleEntries = useMemo(
    () => getVisibleEntries(entryResponse?.entries ?? [], route),
    [entryResponse?.entries, route],
  );

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === '/') {
        event.preventDefault();
        filterInputRef.current?.focus();
        filterInputRef.current?.select();
        return;
      }

      if (event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement || event.target instanceof HTMLSelectElement) {
        return;
      }

      if (event.key === 'g') {
        event.preventDefault();
        navigateTo({ ...route, view: 'dashboard' });
      } else if (event.key === 'q') {
        event.preventDefault();
        navigateTo({ ...route, view: 'queues' });
      } else if (event.key === 'w' && route.workerId) {
        event.preventDefault();
        navigateTo({ ...route, view: 'worker' });
      } else if (event.key === 'o') {
        event.preventDefault();
        navigateTo({ ...route, view: 'operations' });
      } else if ((event.key === 'j' || event.key === 'k') && visibleEntries.length) {
        event.preventDefault();
        const nextEntry = stepSelection(visibleEntries, route.entryId, event.key === 'j' ? 1 : -1);
        if (nextEntry) {
          navigateTo({ ...route, entryId: nextEntry.entryId, workerId: nextEntry.workerId ?? route.workerId }, false);
        }
      }
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [route, visibleEntries]);

  const selectedEntry = useMemo(
    () => chooseSelectedEntry(visibleEntries, route.entryId),
    [visibleEntries, route.entryId],
  );

  useEffect(() => {
    if (!selectedEntry) {
      return;
    }

    const nextWorkerId = selectedEntry.workerId ?? route.workerId;
    if (selectedEntry.entryId === route.entryId && nextWorkerId === route.workerId) {
      return;
    }

    const normalized = normalizeRoute(
      {
        ...route,
        entryId: selectedEntry.entryId,
        workerId: nextWorkerId,
      },
      status,
    );
    setRoute(normalized);
    syncRouteState(normalized, false);
  }, [route, selectedEntry, status]);

  const runBuckets = useMemo(() => {
    if (!runDetail) {
      return [];
    }

    return BUCKET_ORDER.filter((bucket) => (runDetail.bucketCounts[bucket] ?? 0) > 0).map((bucket) => ({
      bucket,
      count: runDetail.bucketCounts[bucket] ?? 0,
    }));
  }, [runDetail]);

  const dashboardWarnings = status?.warnings ?? [];
  const statusWarnings = dashboardWarnings.filter((warning) => warning !== NON_WINDOWS_AD_WARNING);
  const showAdProbeNote = dashboardWarnings.includes(NON_WINDOWS_AD_WARNING);
  const reportLinks = useMemo(() => buildReportLinks(status), [status]);
  const currentViewLabel = getCurrentViewLabel(route.view);

  return (
    <div className="app-shell">
      <header className="hero">
        <div className="portal-breadcrumbs" aria-label="Breadcrumb">
          <span>Home</span>
          <span aria-hidden="true">/</span>
          <span>SyncFactors</span>
          <span aria-hidden="true">/</span>
          <span>{currentViewLabel}</span>
        </div>
        <div className="hero-topbar">
          <div className="hero-copy">
            <p className="eyebrow">SyncFactors Operator UI</p>
            <div className="hero-title-row">
              <h1>Operations Console</h1>
            </div>
            <p className="hero-context">Operator workspace</p>
          </div>
          <div className="hero-meta">
            <span className="badge">TUI parity</span>
            <details className="report-menu" ref={reportMenuRef}>
              <summary className="hero-path">
                <span className="hero-path-label">Reports</span>
                <span aria-hidden="true" className="report-menu-caret">▾</span>
              </summary>
              <div className="report-menu-list">
                {reportLinks.map((link) => (
                  <button
                    key={`${link.label}:${link.path}`}
                    className="report-menu-item"
                    onClick={() => {
                      void handleOpenPath(link.path);
                    }}
                    type="button"
                  >
                    <span className="report-menu-item-label">{link.label}</span>
                    <span className="report-menu-item-path" title={link.path}>{link.path}</span>
                  </button>
                ))}
              </div>
            </details>
            <button
              aria-label={`Switch to ${theme === 'light' ? 'dark' : 'light'} theme`}
              aria-pressed={theme === 'dark'}
              className="theme-switch"
              onClick={() => setTheme((current) => (current === 'light' ? 'dark' : 'light'))}
              type="button"
            >
              <span aria-hidden="true" className="theme-switch-icon">☀</span>
              <span className="theme-switch-track" data-enabled={theme === 'dark'}>
                <span className="theme-switch-thumb" />
              </span>
              <span aria-hidden="true" className="theme-switch-icon">☾</span>
            </button>
          </div>
        </div>
        <div className="hero-toolbar">
          <nav className="view-nav" aria-label="Primary">
            <button className={route.view === 'dashboard' ? 'active' : ''} onClick={() => navigateTo({ ...route, view: 'dashboard' })} type="button">Dashboard</button>
            <button className={route.view === 'report' ? 'active' : ''} onClick={() => navigateTo({ ...route, view: 'report' })} type="button" disabled={!route.runId}>Report</button>
            <button className={route.view === 'queues' ? 'active' : ''} onClick={() => navigateTo({ ...route, view: 'queues' })} type="button">Queues</button>
            <button className={route.view === 'worker' ? 'active' : ''} onClick={() => navigateTo({ ...route, view: 'worker' })} type="button" disabled={!route.workerId}>Worker</button>
            <button className={route.view === 'operations' ? 'active' : ''} onClick={() => navigateTo({ ...route, view: 'operations' })} type="button">Operations</button>
          </nav>
          <div className="portal-command-meta">
            <span>Context: {currentViewLabel}</span>
          </div>
        </div>
      </header>

      {error ? <section className="error-banner">{error}</section> : null}
      {statusWarnings.length ? <WarningPanel title="Status warnings" warnings={statusWarnings} /> : null}
      {showAdProbeNote ? <StatusNote>Active Directory health is unavailable on this macOS host.</StatusNote> : null}

      {route.view === 'dashboard' ? (
        <DashboardView
          status={status}
          route={route}
          runDetail={runDetail}
          entryResponse={entryResponse}
          selectedEntry={selectedEntry}
          runBuckets={runBuckets}
          filterInputRef={filterInputRef}
          onSelectRun={(runId) => navigateTo({ ...route, view: 'dashboard', runId, entryId: null })}
          onSelectBucket={(bucket) => navigateTo({
            ...route,
            bucket,
            reviewExplorer: bucket === 'creates' ? 'created' : bucket === 'deletions' ? 'deleted' : 'changed',
            entryId: null,
          })}
          onFilterChange={(filter) => navigateTo({ ...route, filter })}
          onSelectEntry={(entry) => navigateTo({ ...route, entryId: entry.entryId, workerId: entry.workerId ?? route.workerId }, false)}
          onChangeDiffMode={(diffMode) => navigateTo({ ...route, diffMode }, false)}
          onChangeReviewExplorer={(reviewExplorer) =>
            navigateTo({ ...route, reviewExplorer, bucket: mapReviewExplorerToBucket(reviewExplorer), entryId: null })
          }
          onOpenWorker={(workerId) => navigateTo({ ...route, view: 'worker', workerId })}
          onOpenReport={() => navigateTo({ ...route, view: 'report' })}
        />
      ) : null}

      {route.view === 'report' ? (
        <ReportExplorerView
          route={route}
          runDetail={runDetail}
          entryResponse={entryResponse}
          selectedEntry={selectedEntry}
          filterInputRef={filterInputRef}
          onCategoryChange={(reportCategory) => navigateTo({ ...route, reportCategory, entryId: null })}
          onFilterChange={(filter) => navigateTo({ ...route, filter })}
          onSelectEntry={(entry) => navigateTo({ ...route, entryId: entry.entryId, workerId: entry.workerId ?? route.workerId }, false)}
          onDiffModeChange={(diffMode) => navigateTo({ ...route, diffMode }, false)}
          onOpenWorker={(workerId) => navigateTo({ ...route, view: 'worker', workerId })}
          onOpenPath={() => setPendingConfirmation({
            kind: 'run-open',
            descriptor: { title: 'Open report path', message: 'Open the selected report path in the default app.', requiredText: 'YES', riskLevel: 'low' },
            confirmText: '',
          })}
          onCopyPath={() => setPendingConfirmation({
            kind: 'run-copy',
            descriptor: { title: 'Copy report path', message: 'Copy the selected report path to the clipboard.', requiredText: 'YES', riskLevel: 'medium' },
            confirmText: '',
          })}
          onExport={() => setPendingConfirmation({
            kind: 'run-export',
            descriptor: { title: 'Export bucket selection', message: 'Export the selected bucket and active filter to a JSON file in the temp directory.', requiredText: 'YES', riskLevel: 'medium' },
            confirmText: '',
          })}
        />
      ) : null}

      {route.view === 'queues' ? (
        <QueueView
          route={route}
          queueResponse={queueResponse}
          filterInputRef={filterInputRef}
          onQueueChange={(queueName) => navigateTo({ ...route, queueName, reason: '', reviewCaseType: '', workerId: null, page: 1 })}
          onFilterChange={(filter) => navigateTo({ ...route, filter, page: 1 })}
          onReasonChange={(reason) => navigateTo({ ...route, reason, page: 1 })}
          onReviewCaseChange={(reviewCaseType) => navigateTo({ ...route, reviewCaseType, page: 1 })}
          onPageChange={(page) => navigateTo({ ...route, page })}
          onPageSizeChange={(pageSize) => navigateTo({ ...route, pageSize, page: 1 })}
          onOpenWorker={(workerId) => navigateTo({ ...route, view: 'worker', workerId })}
          onOpenRun={(entry) => navigateTo({
            ...route,
            view: 'report',
            runId: entry.runId,
            bucket: entry.bucket,
            entryId: entry.entryId,
            workerId: entry.workerId,
            reportCategory: mapEntryToReportCategory(entry),
          })}
        />
      ) : null}

      {route.view === 'worker' ? (
        <WorkerView
          route={route}
          workerDetail={workerDetail}
          workerActionState={workerActionState}
          onRunWorkerAction={(action) => void handleRunWorkerAction(action)}
          onOpenRun={(runId, entry) => navigateTo({
            ...route,
            view: 'report',
            runId,
            bucket: entry?.bucket ?? route.bucket,
            entryId: entry?.entryId ?? null,
            workerId: entry?.workerId ?? route.workerId,
            reportCategory: entry ? mapEntryToReportCategory(entry) : route.reportCategory,
          })}
        />
      ) : null}

      {route.view === 'operations' ? (
        <OperationsView
          status={status}
          pendingActionLabel={pendingOperatorActionLabel}
          latestResult={commandResult}
          recentResults={recentCommandResults}
          streamConnected={streamConnected}
          workerLauncherId={operationsWorkerId}
          workerLauncherMode={operationsPreviewMode}
          onRunAction={(action) => void handleRunOperatorAction(action)}
          onRunPreflight={() => void handleRunPreflight()}
          onRunFreshReset={() => setPendingConfirmation({
            kind: 'fresh-reset',
            descriptor: {
              title: 'Fresh sync reset',
              message: 'Delete managed AD user objects and reset local sync state.',
              requiredText: 'DELETE',
              riskLevel: 'critical',
            },
            confirmText: '',
            countText: '',
            destructiveText: '',
          })}
          onOpenLatestRun={(runId) => {
            if (runId) {
              navigateTo({ ...route, view: 'report', runId, entryId: null });
            }
          }}
          onWorkerLauncherIdChange={setOperationsWorkerId}
          onWorkerLauncherModeChange={setOperationsPreviewMode}
          onPreviewWorker={() => void handlePreviewWorker(operationsWorkerId.trim(), operationsPreviewMode)}
          onOpenWorker={() => navigateTo({ ...route, view: 'worker', workerId: operationsWorkerId.trim() || route.workerId })}
        />
      ) : null}

      {workerPreview ? (
        <WorkerPreviewPanel
          preview={workerPreview}
          onApply={() => setPendingConfirmation({
            kind: 'worker-apply',
            descriptor: { title: 'Apply worker sync', message: `Apply worker sync for ${workerPreview.preview.workerId}.`, requiredText: 'YES', riskLevel: 'high' },
            confirmText: '',
            workerId: workerPreview.preview.workerId,
          })}
          onOpenRun={() => {
            if (workerPreview.runId) {
              navigateTo({ ...route, view: 'report', runId: workerPreview.runId, workerId: workerPreview.preview.workerId, entryId: null });
              setWorkerPreview(null);
            }
          }}
          onClose={() => setWorkerPreview(null)}
        />
      ) : null}

      {pendingConfirmation ? renderConfirmationDialog(pendingConfirmation) : null}
      <CommandResultPanel result={commandResult} onClose={() => setCommandResult(null)} onCopyPath={(value) => void navigator.clipboard?.writeText(value)} />
    </div>
  );

  function renderConfirmationDialog(confirmation: PendingConfirmation) {
    if (confirmation.kind === 'fresh-reset') {
      return (
        <ConfirmationDialog
          descriptor={confirmation.descriptor}
          value={confirmation.confirmText}
          onChange={(confirmText) => setPendingConfirmation({ ...confirmation, confirmText })}
          extraFields={[
            { label: 'Object count', value: confirmation.countText, onChange: (countText) => setPendingConfirmation({ ...confirmation, countText }) },
            { label: 'Final phrase', value: confirmation.destructiveText, onChange: (destructiveText) => setPendingConfirmation({ ...confirmation, destructiveText }) },
          ]}
          onConfirm={() => void confirmPendingAction()}
          onClose={() => setPendingConfirmation(null)}
        />
      );
    }

    return (
      <ConfirmationDialog
        descriptor={confirmation.descriptor}
        value={confirmation.confirmText}
        onChange={(confirmText) => setPendingConfirmation({ ...confirmation, confirmText })}
        onConfirm={() => void confirmPendingAction()}
        onClose={() => setPendingConfirmation(null)}
      />
    );
  }

  function navigateTo(nextRoute: RouteState, push = true) {
    setRouteAndUrl(nextRoute, push);
  }

  function setRouteAndUrl(nextRoute: RouteState, push: boolean) {
    const normalized = normalizeRoute(nextRoute, status);
    setRoute(normalized);
    syncRouteState(normalized, push);
  }

  async function handleOpenPath(path: string) {
    try {
      await openLocalPath(path);
      reportMenuRef.current?.removeAttribute('open');
    } catch (openError) {
      setError(openError instanceof Error ? openError.message : 'Failed to open the selected path.');
    }
  }

  async function refreshAfterAction(workerId = route.workerId) {
    const nextStatus = await getStatus();
    setStatus(nextStatus);
    if (workerId) {
      const nextWorkerDetail = await getWorkerDetail(workerId);
      setWorkerDetail(nextWorkerDetail);
    }
  }

  async function handleRunWorkerAction(action: WorkerActionKind) {
    if (!route.workerId || workerActionState.pendingAction) {
      return;
    }

    setError(null);
    setWorkerActionState({ pendingAction: action, result: null });

    try {
      if (action === 'test-sync' || action === 'review-sync') {
        const previewMode: WorkerPreviewMode = action === 'test-sync' ? 'minimal' : 'full';
        const preview = await previewWorker(route.workerId, previewMode);
        setWorkerPreview(preview);
      } else {
        const result = await runWorkerAction(route.workerId, action);
        setWorkerActionState({ pendingAction: null, result });
        if (result.result.runId) {
          navigateTo({ ...route, view: 'report', runId: result.result.runId, workerId: route.workerId, entryId: null });
        }
        await refreshAfterAction(route.workerId);
        return;
      }

      setWorkerActionState({ pendingAction: null, result: null });
      await refreshAfterAction(route.workerId);
    } catch (actionError) {
      setWorkerActionState({ pendingAction: null, result: null });
      setError(actionError instanceof Error ? actionError.message : 'Failed to run worker action.');
    }
  }

  async function handleRunOperatorAction(action: OperatorActionKind) {
    try {
      setError(null);
      setPendingOperatorActionLabel(getOperatorActionLabel(action));
      const result = await runOperatorAction(action);
      recordCommandResult(result);
      if (result.runId) {
        navigateTo({ ...route, view: 'report', runId: result.runId, entryId: null }, false);
      }
      await refreshAfterAction();
    } catch (actionError) {
      setError(actionError instanceof Error ? actionError.message : 'Failed to run operator action.');
    } finally {
      setPendingOperatorActionLabel(null);
    }
  }

  async function handleRunPreflight() {
    try {
      setError(null);
      setPendingOperatorActionLabel('Preflight');
      const result = await runPreflight();
      recordCommandResult(result);
    } catch (actionError) {
      setError(actionError instanceof Error ? actionError.message : 'Failed to run preflight.');
    } finally {
      setPendingOperatorActionLabel(null);
    }
  }

  async function handlePreviewWorker(workerId: string, previewMode: WorkerPreviewMode) {
    if (!workerId) {
      return;
    }

    try {
      setError(null);
      setPendingOperatorActionLabel(`Worker preview ${workerId}`);
      const preview = await previewWorker(workerId, previewMode);
      setWorkerPreview(preview);
      setOperationsWorkerId(workerId);
      await refreshAfterAction(workerId);
    } catch (previewError) {
      setError(previewError instanceof Error ? previewError.message : 'Failed to preview worker.');
    } finally {
      setPendingOperatorActionLabel(null);
    }
  }

  async function confirmPendingAction() {
    if (!pendingConfirmation) {
      return;
    }

    try {
      setError(null);
      if (pendingConfirmation.kind === 'worker-apply') {
        const result = await applyWorker(pendingConfirmation.workerId, pendingConfirmation.confirmText);
        setWorkerActionState({ pendingAction: null, result });
        setPendingConfirmation(null);
        setWorkerPreview(null);
        await refreshAfterAction(pendingConfirmation.workerId);
        return;
      }

      if (pendingConfirmation.kind === 'run-open' && route.runId) {
        const result = await openRunReport(route.runId, pendingConfirmation.confirmText);
        recordCommandResult(result);
      } else if (pendingConfirmation.kind === 'run-copy' && route.runId) {
        const result = await copyRunReportPath(route.runId, pendingConfirmation.confirmText);
        recordCommandResult(result);
        if (result.reportPath) {
          await navigator.clipboard?.writeText(result.reportPath);
        }
      } else if (pendingConfirmation.kind === 'run-export' && route.runId) {
        const result = await exportRunBucket(route.runId, route.bucket, route.filter, pendingConfirmation.confirmText);
        recordCommandResult(result);
      } else if (pendingConfirmation.kind === 'fresh-reset') {
        const result = await runFreshReset(
          pendingConfirmation.confirmText,
          [pendingConfirmation.countText, pendingConfirmation.destructiveText],
        );
        recordCommandResult(result);
        await refreshAfterAction();
      }

      setPendingConfirmation(null);
    } catch (actionError) {
      setError(actionError instanceof Error ? actionError.message : 'Confirmation action failed.');
    }
  }

  function recordCommandResult(result: OperatorCommandResult) {
    setCommandResult(result);
    setRecentCommandResults((current) => [result, ...current].slice(0, 8));
  }
}

function getInitialTheme(): ThemeMode {
  if (typeof window === 'undefined') {
    return 'light';
  }

  if (!hasStorageAccess()) {
    return 'light';
  }

  const storedTheme = window.localStorage.getItem(THEME_STORAGE_KEY);
  if (storedTheme === 'light' || storedTheme === 'dark') {
    return storedTheme;
  }

  return 'light';
}

function hasStorageAccess(): boolean {
  return typeof window !== 'undefined'
    && typeof window.localStorage !== 'undefined'
    && typeof window.localStorage.getItem === 'function'
    && typeof window.localStorage.setItem === 'function';
}

function buildReportLinks(status: DashboardStatus | null): Array<{ label: string; path: string }> {
  if (!status) {
    return [];
  }

  const candidates = [
    { label: 'Output dir', path: status.paths.reportDirectory },
    { label: 'Review dir', path: status.paths.reviewReportDirectory },
    { label: 'Runtime', path: status.paths.runtimeStatusPath },
    { label: 'State', path: status.paths.statePath },
    { label: 'Config', path: status.paths.configPath },
    ...status.recentRuns
      .filter((run) => Boolean(run.path))
      .slice(0, 5)
      .map((run, index) => ({
        label: index === 0 ? 'Latest run' : `Run ${index + 1}`,
        path: run.path!,
      })),
  ];

  const seen = new Set<string>();
  return candidates.filter((candidate) => {
    if (!candidate.path || seen.has(candidate.path)) {
      return false;
    }

    seen.add(candidate.path);
    return true;
  });
}

function getCurrentViewLabel(view: RouteState['view']): string {
  switch (view) {
    case 'queues':
      return 'Queues';
    case 'worker':
      return 'Worker';
    case 'report':
      return 'Report Explorer';
    case 'worker-preview':
      return 'Worker Preview';
    case 'operations':
      return 'Operations';
    default:
      return 'Dashboard';
  }
}

function getVisibleEntries(entries: EntryListResponse['entries'], route: RouteState): EntryListResponse['entries'] {
  if (route.view !== 'report') {
    return entries;
  }

  return entries.filter((entry) => mapEntryToReportCategory(entry) === route.reportCategory);
}

function mapReportCategoryToBucket(category: 'Changed' | 'Created' | 'Deleted'): string | undefined {
  if (category === 'Created') {
    return 'creates';
  }
  if (category === 'Deleted') {
    return 'deletions';
  }
  return undefined;
}

function getOperatorActionLabel(action: OperatorActionKind): string {
  switch (action) {
    case 'delta-dry-run':
      return 'Delta dry-run';
    case 'delta-sync':
      return 'Delta sync';
    case 'full-dry-run':
      return 'Full dry-run';
    case 'full-sync':
      return 'Full sync';
    case 'review-run':
      return 'First-sync review';
  }
}
