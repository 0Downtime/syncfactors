import { useEffect, useMemo, useRef, useState } from 'react';
import { getQueue, getRun, getRunEntries, getStatus, getWorkerDetail, openLocalPath, runWorkerAction } from './api.js';
import { BUCKET_ORDER, chooseSelectedEntry, DEFAULT_ROUTE, getRouteState, mapReviewExplorerToBucket, normalizeRoute, resolveActiveBucket, stepSelection, syncRouteState } from './route-state.js';
import type { RouteState } from './route-state.js';
import { StatusNote, WarningPanel } from './triage-components.js';
import { DashboardView, QueueView, WorkerView } from './triage-views.js';
import type { DashboardStatus, EntryListResponse, EntryRecord, QueueResponse, RunDetailResponse, WorkerActionKind, WorkerActionResponse, WorkerDetailResponse } from './types.js';

type ThemeMode = 'light' | 'dark';

const THEME_STORAGE_KEY = 'syncfactors-theme';

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
  const [error, setError] = useState<string | null>(null);
  const [theme, setTheme] = useState<ThemeMode>(() => getInitialTheme());
  const filterInputRef = useRef<HTMLInputElement | null>(null);
  const reportMenuRef = useRef<HTMLDetailsElement | null>(null);

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
    const loadStatus = async () => {
      try {
        const nextStatus = await getStatus();
        if (cancelled) {
          return;
        }

        setStatus(nextStatus);
        setRoute((current) => {
          const nextRunId = current.runId ?? nextStatus.recentRuns[0]?.runId ?? null;
          const nextWorkerId = current.workerId ?? nextStatus.recentRuns[0]?.workerScope?.workerId ?? null;
          const next = { ...current, runId: nextRunId, workerId: nextWorkerId };
          syncRouteState(next);
          return next;
        });
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load dashboard status.');
        }
      }
    };

    void loadStatus();
    const interval = window.setInterval(() => void loadStatus(), 10000);
    return () => {
      cancelled = true;
      window.clearInterval(interval);
    };
  }, []);

  useEffect(() => {
    if (!route.runId) {
      setRunDetail(null);
      setEntryResponse(null);
      return;
    }

    const runId = route.runId;
    let cancelled = false;
    void (async () => {
      try {
        const [nextRunDetail, nextEntries] = await Promise.all([
          getRun(runId),
          getRunEntries(runId, {
            bucket: resolveActiveBucket(route.bucket, route.reviewExplorer),
            filter: route.filter || undefined,
          }),
        ]);
        if (cancelled) {
          return;
        }

        setRunDetail(nextRunDetail);
        setEntryResponse(nextEntries);
        const resolvedEntry = chooseSelectedEntry(nextEntries.entries, route.entryId);
        if (resolvedEntry && resolvedEntry.entryId !== route.entryId) {
          const nextRoute = {
            ...route,
            entryId: resolvedEntry.entryId,
            workerId: route.view === 'worker' ? route.workerId : (resolvedEntry.workerId ?? route.workerId),
          };
          setRouteAndUrl(nextRoute, false);
        } else if (!resolvedEntry && nextEntries.entries[0]) {
          const nextRoute = {
            ...route,
            entryId: nextEntries.entries[0].entryId,
            workerId: route.view === 'worker' ? route.workerId : (nextEntries.entries[0].workerId ?? route.workerId),
          };
          setRouteAndUrl(nextRoute, false);
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
  }, [route.runId, route.bucket, route.filter, route.reviewExplorer]);

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

    const workerId = route.workerId;
    let cancelled = false;
    void (async () => {
      try {
        const nextWorker = await getWorkerDetail(workerId);
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
    setWorkerActionState({ pendingAction: null, result: null });
  }, [route.workerId]);

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
      } else if (event.key === 'j' || event.key === 'k') {
        event.preventDefault();
        if (route.view === 'dashboard' && entryResponse?.entries?.length) {
          const nextEntry = stepSelection(entryResponse.entries, route.entryId, event.key === 'j' ? 1 : -1);
          if (nextEntry) {
            navigateTo({ ...route, entryId: nextEntry.entryId, workerId: nextEntry.workerId ?? route.workerId }, false);
          }
        }
      } else if (route.view === 'queues' && event.key === 'n' && queueResponse && route.page < Math.ceil(queueResponse.total / route.pageSize)) {
        event.preventDefault();
        navigateTo({ ...route, page: route.page + 1 });
      } else if (route.view === 'queues' && event.key === 'p' && route.page > 1) {
        event.preventDefault();
        navigateTo({ ...route, page: route.page - 1 });
      }
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [route, entryResponse, queueResponse]);

  const selectedEntry = useMemo(
    () => chooseSelectedEntry(entryResponse?.entries ?? [], route.entryId),
    [entryResponse?.entries, route.entryId],
  );

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
  const adProbeSkipWarning = 'Active Directory health probe is skipped on non-Windows hosts for the web dashboard.';
  const statusWarnings = dashboardWarnings.filter((warning) => warning !== adProbeSkipWarning);
  const showAdProbeNote = dashboardWarnings.includes(adProbeSkipWarning);
  const reportLinks = useMemo(() => buildReportLinks(status), [status]);
  const currentViewLabel = route.view === 'queues' ? 'Queues' : route.view === 'worker' ? 'Worker' : 'Dashboard';

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
            <span className="badge">Scoped worker actions</span>
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
            <button className={route.view === 'queues' ? 'active' : ''} onClick={() => navigateTo({ ...route, view: 'queues' })} type="button">Queues</button>
            <button className={route.view === 'worker' ? 'active' : ''} onClick={() => navigateTo({ ...route, view: 'worker' })} type="button" disabled={!route.workerId}>Worker</button>
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
          onOpenRun={(entry) => navigateTo({ ...route, view: 'dashboard', runId: entry.runId, bucket: entry.bucket, entryId: entry.entryId, workerId: entry.workerId })}
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
            view: 'dashboard',
            runId,
            bucket: entry?.bucket ?? route.bucket,
            entryId: entry?.entryId ?? null,
            workerId: entry?.workerId ?? route.workerId,
          })}
        />
      ) : null}
    </div>
  );

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

  async function handleRunWorkerAction(action: WorkerActionKind) {
    if (!route.workerId || workerActionState.pendingAction) {
      return;
    }

    setError(null);
    setWorkerActionState({ pendingAction: action, result: null });

    try {
      const result = await runWorkerAction(route.workerId, action);
      setWorkerActionState({ pendingAction: null, result });
      const nextStatus = await getStatus();
      setStatus(nextStatus);
      const nextWorkerDetail = await getWorkerDetail(route.workerId);
      setWorkerDetail(nextWorkerDetail);
    } catch (actionError) {
      setWorkerActionState({ pendingAction: null, result: null });
      setError(actionError instanceof Error ? actionError.message : 'Failed to run worker action.');
    }
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
