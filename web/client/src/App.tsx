import { FormEvent, useEffect, useMemo, useState } from 'react';
import {
  applyPreview,
  cancelRun,
  createPreview,
  createUser,
  deleteUser,
  getDashboard,
  getHealth,
  getPreview,
  getPreviewHistory,
  getRun,
  getRunEntries,
  getSession,
  getSyncSchedule,
  listRuns,
  listUsers,
  login,
  logout,
  queueDeleteAllUsers,
  resetUserPassword,
  saveSyncSchedule,
  setUserActive,
  setUserRole,
  startRun,
} from './api.js';
import { ApplyResultDetails, DiagnosticsSection, RunEntryDiagnostics, SourceConfidenceSections } from './components.js';
import { buildRiskCallouts } from './preview-risk.js';
import { buildLoginPath, buildRunPath, navigate, parseRoute } from './router.js';
import type {
  DashboardSnapshot,
  DependencyHealthSnapshot,
  DirectoryCommandResult,
  LocalUserSummary,
  RunDetail,
  RunSummary,
  RunEntriesResponse,
  Session,
  SyncScheduleStatus,
  WorkerPreviewHistoryItem,
  WorkerPreviewResult,
} from './types.js';
import { badgeClass, displayBool, enableTransition, formatTimestamp, getInitialTheme, getSavedPreviewRunId, runSummary, THEME_KEY } from './ui-utils.js';

type Theme = 'light' | 'dark';
type Flash = { tone: 'good' | 'danger' | 'warn'; message: string } | null;
type Route = import('./router.js').Route;
const RUNS_PAGE_SIZE = 25;
const ENTRY_PAGE_SIZE = 50;
const DELETE_ALL_CONFIRMATION = 'DELETE ALL USERS';

export function App() {
  const [route, setRoute] = useState<Route>(() => parseRoute(window.location));
  const [session, setSession] = useState<Session | null>(null);
  const [sessionReady, setSessionReady] = useState(false);
  const [theme, setTheme] = useState<Theme>(() => getInitialTheme());
  const [flash, setFlash] = useState<Flash>(null);

  useEffect(() => {
    const onPopState = () => setRoute(parseRoute(window.location));
    window.addEventListener('popstate', onPopState);
    return () => window.removeEventListener('popstate', onPopState);
  }, []);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    document.documentElement.style.colorScheme = theme;
    try {
      window.localStorage.setItem(THEME_KEY, theme);
    } catch {
      // Ignore storage errors.
    }
  }, [theme]);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const nextSession = await getSession();
        if (!cancelled) {
          setSession(nextSession);
        }
      } catch {
        if (!cancelled) {
          setSession({
            isAuthenticated: false,
            userId: null,
            username: null,
            role: null,
            isAdmin: false,
          });
        }
      } finally {
        if (!cancelled) {
          setSessionReady(true);
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (!sessionReady || !session) {
      return;
    }

    if (!session.isAuthenticated && route.kind !== 'login') {
      navigate(buildLoginPath(window.location.pathname + window.location.search), setRoute);
      return;
    }

    if (session.isAuthenticated && route.kind === 'login') {
      navigate(route.returnUrl || '/', setRoute, true);
    }
  }, [route, session, sessionReady]);

  if (!sessionReady || !session) {
    return <div className="shell"><main className="content"><section className="panel"><p className="muted">Loading session…</p></section></main></div>;
  }

  if (!session.isAuthenticated || route.kind === 'login') {
    return (
      <LoginPage
        returnUrl={route.kind === 'login' ? route.returnUrl : null}
        onLoggedIn={(nextSession, returnUrl) => {
          setSession(nextSession);
          setFlash({ tone: 'good', message: 'Signed in.' });
          navigate(returnUrl || '/', setRoute, true);
        }}
      />
    );
  }

  return (
    <div className="shell">
      <header className="topbar">
        <div className="topbar-brand">
          <div className="brand-row">
            <button className="brand brand-button" type="button" onClick={() => navigate('/', setRoute)}>SyncFactors</button>
            <span className="brand-badge">Portal</span>
          </div>
          <p className="subtitle">Operator dashboard for the .NET 10 rewrite</p>
        </div>
        <div className="topbar-actions">
          <nav className="nav">
            <button type="button" className={route.kind === 'dashboard' ? 'active' : ''} onClick={() => navigate('/', setRoute)}>Dashboard</button>
            <button type="button" className={route.kind === 'sync' ? 'active' : ''} onClick={() => navigate('/sync', setRoute)}>Sync</button>
            <button type="button" className={route.kind === 'preview' ? 'active' : ''} onClick={() => navigate('/preview', setRoute)}>Worker Preview</button>
            {session.isAdmin ? (
              <button type="button" className={route.kind === 'users' ? 'active' : ''} onClick={() => navigate('/admin/users', setRoute)}>Users</button>
            ) : null}
          </nav>
          <div className="auth-actions">
            <span className="muted auth-user">Signed in as <strong>{session.username}</strong></span>
            <button
              type="button"
              className="link-button"
              onClick={async () => {
                await logout();
                setSession({
                  isAuthenticated: false,
                  userId: null,
                  username: null,
                  role: null,
                  isAdmin: false,
                });
                setFlash(null);
                navigate('/login', setRoute, true);
              }}
            >
              Logout
            </button>
          </div>
          <button
            className="theme-toggle"
            type="button"
            aria-pressed={theme === 'dark'}
            onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}
          >
            {theme === 'dark' ? 'Dark' : 'Light'}
          </button>
        </div>
      </header>
      <main className="content">
        {flash ? <p className={`callout ${flash.tone}`}>{flash.message}</p> : null}
        {route.kind === 'dashboard' ? <DashboardPage onOpenRun={(runId) => navigate(`/runs/${encodeURIComponent(runId)}`, setRoute)} onOpenSync={() => navigate('/sync', setRoute)} /> : null}
        {route.kind === 'sync' ? (
          <SyncPage
            page={route.page}
            onNavigatePage={(page) => navigate(`/sync?page=${page}`, setRoute)}
            onOpenRun={(runId) => navigate(`/runs/${encodeURIComponent(runId)}`, setRoute)}
            onFlash={setFlash}
          />
        ) : null}
        {route.kind === 'preview' ? (
          <PreviewPage
            route={route}
            onNavigate={(nextPath) => navigate(nextPath, setRoute)}
            onOpenRun={(runId) => navigate(`/runs/${encodeURIComponent(runId)}`, setRoute)}
            onFlash={setFlash}
          />
        ) : null}
        {route.kind === 'run' ? (
          <RunDetailPage
            route={route}
            onNavigate={(nextPath) => navigate(nextPath, setRoute)}
          />
        ) : null}
        {route.kind === 'users' ? <UsersPage currentUserId={session.userId} onFlash={setFlash} /> : null}
      </main>
    </div>
  );
}

function LoginPage(props: { returnUrl: string | null; onLoggedIn: (session: Session, returnUrl: string | null) => void }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [rememberMe, setRememberMe] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      const session = await login(username, password, rememberMe, props.returnUrl);
      props.onLoggedIn(session, props.returnUrl);
    } catch (submitError) {
      setError(submitError instanceof Error ? submitError.message : 'Sign-in failed.');
    } finally {
      setPending(false);
    }
  }

  return (
    <div className="shell">
      <main className="content">
        <section className="hero hero-compact auth-hero">
          <div>
            <p className="eyebrow">Local Access</p>
            <h1>Sign in</h1>
            <p className="lede">Use a local operator account to access the SyncFactors portal.</p>
          </div>
        </section>

        <section className="panel auth-panel">
          {error ? <p className="callout danger">{error}</p> : null}
          <form className="filters auth-form" onSubmit={handleSubmit}>
            <label>
              <span>Username</span>
              <input value={username} onChange={(event) => setUsername(event.target.value)} autoComplete="username" />
            </label>
            <label>
              <span>Password</span>
              <input type="password" value={password} onChange={(event) => setPassword(event.target.value)} autoComplete="current-password" />
            </label>
            <label className="filter-check auth-remember">
              <span>Remember my login</span>
              <input type="checkbox" checked={rememberMe} onChange={(event) => setRememberMe(event.target.checked)} />
            </label>
            <div className="filter-actions">
              <button type="submit" disabled={pending}>{pending ? 'Signing in…' : 'Sign in'}</button>
            </div>
          </form>
        </section>
      </main>
    </div>
  );
}

function DashboardPage(props: { onOpenRun: (runId: string) => void; onOpenSync: () => void }) {
  const [dashboard, setDashboard] = useState<DashboardSnapshot | null>(null);
  const [health, setHealth] = useState<DependencyHealthSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    const load = async () => {
      try {
        const [nextDashboard, nextHealth] = await Promise.all([getDashboard(), getHealth()]);
        if (!cancelled) {
          setDashboard(nextDashboard);
          setHealth(nextHealth);
          setError(null);
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load dashboard.');
        }
      }
    };

    void load();
    const timer = window.setInterval(() => void load(), 15000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, []);

  const healthByName = useMemo(() => new Map((health?.probes ?? []).map((probe) => [probe.dependency, probe])), [health]);

  return (
    <>
      <section className="hero">
        <div className="hero-main">
          <div>
            <p className="eyebrow">Current Runtime</p>
            <h1>Operator Dashboard</h1>
            <p className="lede">This page reads runtime status and recent runs from the configured operational store for the .NET rewrite.</p>
          </div>
          <div className="hero-actions">
            <button type="button" className="link-button" onClick={props.onOpenSync}>Manage sync</button>
          </div>
        </div>
        <aside className="hero-run-health">
          <div className="hero-run-health-header">
            <p className="eyebrow">Run Health</p>
            <span className="badge neutral">Live</span>
          </div>
          <div className="run-health-stack run-health-stack-compact">
            <section className="run-health-card">
              <p className="eyebrow">Active Run</p>
              {dashboard?.activeRun ? (
                <>
                  <p><strong>{dashboard.activeRun.runId}</strong></p>
                  <p className="muted">{dashboard.activeRun.mode} · {dashboard.activeRun.processedWorkers} / {dashboard.activeRun.totalWorkers} workers</p>
                  <p><button type="button" className="inline-link" onClick={() => props.onOpenRun(dashboard.activeRun!.runId)}>Open active run</button></p>
                </>
              ) : <p className="muted">No run is active.</p>}
            </section>
            <section className="run-health-card">
              <p className="eyebrow">Last Completed</p>
              {dashboard?.lastCompletedRun ? (
                <>
                  <p><strong>{dashboard.lastCompletedRun.runId}</strong></p>
                  <p className="muted">{dashboard.lastCompletedRun.status} · {dashboard.lastCompletedRun.mode} · {dashboard.lastCompletedRun.totalWorkers} workers</p>
                  <p><button type="button" className="inline-link" onClick={() => props.onOpenRun(dashboard.lastCompletedRun!.runId)}>Review last run</button></p>
                </>
              ) : <p className="muted">No completed runs yet.</p>}
            </section>
          </div>
        </aside>
      </section>

      {error ? <section className="panel"><p className="callout danger">{error}</p></section> : null}
      {dashboard?.requiresAttention ? <section className="panel"><p className="callout danger">{dashboard.attentionMessage ?? 'Operator attention is required.'}</p></section> : null}

      <section className="panel-grid dashboard-grid">
        <article className="panel">
          <h2>Status</h2>
          <dl className="kv">
            <div><dt>Status</dt><dd>{dashboard?.status.status ?? 'Loading'}</dd></div>
            <div><dt>Stage</dt><dd>{dashboard?.status.stage ?? 'Loading'}</dd></div>
            <div><dt>Run Id</dt><dd>{dashboard?.status.runId ?? 'None'}</dd></div>
            <div><dt>Worker</dt><dd>{dashboard?.status.currentWorkerId ?? 'None'}</dd></div>
            <div><dt>Progress</dt><dd>{dashboard ? `${dashboard.status.processedWorkers} / ${dashboard.status.totalWorkers}` : '0 / 0'}</dd></div>
            <div><dt>Last Action</dt><dd>{dashboard?.status.lastAction ?? 'None'}</dd></div>
            <div><dt>Last Updated</dt><dd>{formatTimestamp(dashboard?.status.lastUpdatedAt)}</dd></div>
          </dl>
          {dashboard?.status.errorMessage ? <p className="callout danger">{dashboard.status.errorMessage}</p> : null}

          <section className="connection-section">
            <div className="section-heading">
              <div>
                <p className="eyebrow">Connection</p>
                <h3>Dependency Health</h3>
              </div>
              <div className="connection-overview">
                <span className={`badge ${badgeClass(health?.status)}`}>{health?.status ?? 'Loading'}</span>
                <span className="muted">{health ? `Last checked ${formatTimestamp(health.checkedAt)}` : 'Probe has not run yet.'}</span>
              </div>
            </div>
            <div className="connection-list">
              {['SuccessFactors', 'Active Directory', 'Worker Service', 'SQLite'].map((name) => {
                const probe = healthByName.get(name);
                return (
                  <article className="connection-card" key={name}>
                    <header className="connection-head">
                      <h4>{name}</h4>
                      <span className={`badge ${badgeClass(probe?.status)}`}>{probe?.status ?? 'Waiting'}</span>
                    </header>
                    <p className="connection-summary">{probe?.summary ?? 'Probe pending.'}</p>
                    <p className="connection-detail muted">{probe?.details ?? 'The dashboard will run this check after the page settles.'}</p>
                  </article>
                );
              })}
            </div>
          </section>
        </article>

        <article className="panel recent-runs-panel">
          <h2>Recent Runs</h2>
          <p className="muted dashboard-refresh-note">Live data is refreshed automatically while this page is open.</p>
          {!dashboard?.runs.length ? (
            <p className="muted">No run data is configured yet. Set <code>SyncFactors__SqlitePath</code> to point the rewrite UI at an operational SQLite store.</p>
          ) : (
            <div className="table-scroll">
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Started</th>
                    <th>Trigger</th>
                    <th>Mode</th>
                    <th>Status</th>
                    <th>Dry Run</th>
                    <th>Workers</th>
                    <th>Summary</th>
                    <th>Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {dashboard.runs.map((run) => (
                    <tr key={run.runId}>
                      <td>{formatTimestamp(run.startedAt)}</td>
                      <td>{run.runTrigger}</td>
                      <td>{run.mode}</td>
                      <td><span className={`badge ${badgeClass(run.status)}`}>{run.status}</span></td>
                      <td>{run.dryRun ? 'Yes' : 'No'}</td>
                      <td>{run.totalWorkers}</td>
                      <td>{runSummary(run)}</td>
                      <td><button type="button" className="inline-link" onClick={() => props.onOpenRun(run.runId)}>Open</button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </article>
      </section>
    </>
  );
}

function SyncPage(props: { page: number; onNavigatePage: (page: number) => void; onOpenRun: (runId: string) => void; onFlash: (flash: Flash) => void }) {
  const [dashboard, setDashboard] = useState<DashboardSnapshot | null>(null);
  const [schedule, setSchedule] = useState<SyncScheduleStatus | null>(null);
  const [runs, setRuns] = useState<RunSummary[]>([]);
  const [totalRuns, setTotalRuns] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [scheduleEnabled, setScheduleEnabled] = useState(false);
  const [intervalMinutes, setIntervalMinutes] = useState(30);
  const [deleteDialogOpen, setDeleteDialogOpen] = useState(false);
  const [deleteConfirmation, setDeleteConfirmation] = useState('');

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const [dashboardSnapshot, scheduleResponse, runResponse] = await Promise.all([
          getDashboard(),
          getSyncSchedule(),
          listRuns(props.page, RUNS_PAGE_SIZE),
        ]);
        if (!cancelled) {
          setDashboard(dashboardSnapshot);
          setSchedule(scheduleResponse.schedule);
          setScheduleEnabled(scheduleResponse.schedule.enabled);
          setIntervalMinutes(scheduleResponse.schedule.intervalMinutes);
          setRuns(runResponse.runs);
          setTotalRuns(runResponse.total);
          setError(null);
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load sync page.');
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [props.page]);

  const totalPages = Math.max(1, Math.ceil(totalRuns / RUNS_PAGE_SIZE));
  const hasPendingOrActiveRun = dashboard?.status.status === 'InProgress';

  async function refresh() {
    const [dashboardSnapshot, scheduleResponse, runResponse] = await Promise.all([
      getDashboard(),
      getSyncSchedule(),
      listRuns(props.page, RUNS_PAGE_SIZE),
    ]);
    setDashboard(dashboardSnapshot);
    setSchedule(scheduleResponse.schedule);
    setScheduleEnabled(scheduleResponse.schedule.enabled);
    setIntervalMinutes(scheduleResponse.schedule.intervalMinutes);
    setRuns(runResponse.runs);
    setTotalRuns(runResponse.total);
  }

  async function handleStartRun(nextMode: 'DryRun' | 'LiveRun') {
    try {
      await startRun(nextMode !== 'LiveRun');
      props.onFlash({ tone: 'good', message: nextMode === 'LiveRun' ? 'Live provisioning run queued.' : 'Dry-run sync queued.' });
      await refresh();
    } catch (runError) {
      setError(runError instanceof Error ? runError.message : 'Failed to queue run.');
    }
  }

  return (
    <>
      <section className="hero hero-compact">
        <div>
          <p className="eyebrow">Provisioning Control</p>
          <h1>Sync</h1>
          <p className="lede">Queue ad hoc AD provisioning runs and manage the recurring full-sync schedule.</p>
        </div>
        <div className="hero-actions">
          <button type="button" className="link-button danger-button" disabled={hasPendingOrActiveRun} onClick={() => setDeleteDialogOpen(true)}>Testing Reset</button>
        </div>
      </section>

      {error ? <section className="panel"><p className="callout danger">{error}</p></section> : null}

      <section className="panel-grid">
        <article className="panel">
          <h2>Current Runtime</h2>
          <dl className="kv">
            <div><dt>Status</dt><dd>{dashboard?.status.status ?? 'Loading'}</dd></div>
            <div><dt>Stage</dt><dd>{dashboard?.status.stage ?? 'Loading'}</dd></div>
            <div><dt>Run Id</dt><dd>{dashboard?.status.runId ?? 'None'}</dd></div>
            <div><dt>Worker</dt><dd>{dashboard?.status.currentWorkerId ?? 'None'}</dd></div>
            <div><dt>Progress</dt><dd>{dashboard ? `${dashboard.status.processedWorkers} / ${dashboard.status.totalWorkers}` : '0 / 0'}</dd></div>
          </dl>

          <div className="filter-actions">
            <button type="button" onClick={() => void handleStartRun('DryRun')} disabled={hasPendingOrActiveRun}>Queue dry run</button>
            <button type="button" onClick={() => void handleStartRun('LiveRun')} disabled={hasPendingOrActiveRun}>Queue live run</button>
            <button
              type="button"
              className="link-button"
              onClick={async () => {
                try {
                  await cancelRun();
                  props.onFlash({ tone: 'good', message: 'Run cancellation requested.' });
                  await refresh();
                } catch (cancelError) {
                  setError(cancelError instanceof Error ? cancelError.message : 'Failed to cancel run.');
                }
              }}
            >
              Cancel run
            </button>
          </div>
        </article>

        <article className="panel">
          <h2>Recurring Schedule</h2>
          <dl className="kv">
            <div><dt>Enabled</dt><dd>{schedule?.enabled ? 'Yes' : 'No'}</dd></div>
            <div><dt>Interval</dt><dd>{schedule?.intervalMinutes ?? 30} minutes</dd></div>
            <div><dt>Next Run</dt><dd>{formatTimestamp(schedule?.nextRunAt)}</dd></div>
            <div><dt>Last Scheduled</dt><dd>{formatTimestamp(schedule?.lastScheduledRunAt)}</dd></div>
            <div><dt>Last Enqueue Attempt</dt><dd>{formatTimestamp(schedule?.lastEnqueueAttemptAt)}</dd></div>
          </dl>
          {schedule?.lastEnqueueError ? <p className="callout warn">{schedule.lastEnqueueError}</p> : null}
          <form
            className="filters"
            onSubmit={async (event) => {
              event.preventDefault();
              try {
                const response = await saveSyncSchedule(scheduleEnabled, intervalMinutes);
                setSchedule(response.schedule);
                props.onFlash({
                  tone: 'good',
                  message: response.schedule.enabled ? `Recurring sync enabled every ${response.schedule.intervalMinutes} minutes.` : 'Recurring sync disabled.',
                });
              } catch (saveError) {
                setError(saveError instanceof Error ? saveError.message : 'Failed to save schedule.');
              }
            }}
          >
            <label>
              <span>Enabled</span>
              <input type="checkbox" checked={scheduleEnabled} onChange={(event) => setScheduleEnabled(event.target.checked)} />
            </label>
            <label>
              <span>Interval minutes</span>
              <input type="number" min={5} max={1440} step={1} value={intervalMinutes} onChange={(event) => setIntervalMinutes(Number(event.target.value))} />
            </label>
            <div className="filter-actions">
              <button type="submit">Save schedule</button>
            </div>
          </form>
        </article>
      </section>

      {deleteDialogOpen ? (
        <div className="dialog-backdrop">
          <section className="confirm-dialog__surface">
            <div className="section-heading">
              <div>
                <p className="eyebrow">Danger Zone</p>
                <h2>Delete All Users</h2>
              </div>
            </div>
            <p className="muted">This queues a live delete job against all users currently returned by the worker source. Type <strong>{DELETE_ALL_CONFIRMATION}</strong> to continue.</p>
            <label>
              <span>Confirmation text</span>
              <input value={deleteConfirmation} onChange={(event) => setDeleteConfirmation(event.target.value)} autoComplete="off" />
            </label>
            <div className="filter-actions">
              <button type="button" className="link-button" onClick={() => setDeleteDialogOpen(false)}>Cancel</button>
              <button
                type="button"
                className="danger-button"
                disabled={deleteConfirmation.trim() !== DELETE_ALL_CONFIRMATION}
                onClick={async () => {
                  try {
                    await queueDeleteAllUsers(deleteConfirmation);
                    props.onFlash({ tone: 'good', message: 'Delete-all test run queued.' });
                    setDeleteDialogOpen(false);
                    setDeleteConfirmation('');
                    await refresh();
                  } catch (deleteError) {
                    setError(deleteError instanceof Error ? deleteError.message : 'Failed to queue delete-all run.');
                  }
                }}
              >
                Queue delete-all run
              </button>
            </div>
          </section>
        </div>
      ) : null}

      <section className="panel">
        <div className="section-heading">
          <div>
            <p className="eyebrow">History</p>
            <h2>Recent Runs</h2>
          </div>
        </div>
        {!runs.length ? (
          <p className="muted">No runs have been recorded yet.</p>
        ) : (
          <>
            <div className="table-scroll">
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Started</th>
                    <th>Trigger</th>
                    <th>Status</th>
                    <th>Scope</th>
                    <th>Dry Run</th>
                    <th>Workers</th>
                    <th>Requested By</th>
                    <th>Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {runs.map((run) => (
                    <tr key={run.runId}>
                      <td>{formatTimestamp(run.startedAt)}</td>
                      <td>{run.runTrigger}</td>
                      <td><span className={`badge ${badgeClass(run.status)}`}>{run.status}</span></td>
                      <td>{run.syncScope}</td>
                      <td>{run.dryRun ? 'Yes' : 'No'}</td>
                      <td>{run.totalWorkers}</td>
                      <td>{run.requestedBy ?? 'n/a'}</td>
                      <td><button type="button" className="inline-link" onClick={() => props.onOpenRun(run.runId)}>Open</button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="table-pagination">
              <p className="muted">Showing page {props.page} of {totalPages} ({totalRuns} total runs).</p>
              <div className="filter-actions">
                {props.page > 1 ? <button type="button" className="link-button" onClick={() => props.onNavigatePage(props.page - 1)}>Previous</button> : null}
                {props.page < totalPages ? <button type="button" className="link-button" onClick={() => props.onNavigatePage(props.page + 1)}>Next</button> : null}
              </div>
            </div>
          </>
        )}
      </section>
    </>
  );
}

function PreviewPage(props: { route: Extract<Route, { kind: 'preview' }>; onNavigate: (path: string) => void; onOpenRun: (runId: string) => void; onFlash: (flash: Flash) => void }) {
  const [lookupWorkerId, setLookupWorkerId] = useState(props.route.workerId ?? '');
  const [preview, setPreview] = useState<WorkerPreviewResult | null>(null);
  const [previousPreview, setPreviousPreview] = useState<WorkerPreviewResult | null>(null);
  const [history, setHistory] = useState<WorkerPreviewHistoryItem[]>([]);
  const [applyResult, setApplyResult] = useState<DirectoryCommandResult | null>(null);
  const [acknowledgeRealSync, setAcknowledgeRealSync] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setLookupWorkerId(props.route.workerId ?? '');
  }, [props.route.workerId]);

  useEffect(() => {
    let cancelled = false;
    setApplyResult(null);

    void (async () => {
      if (!props.route.runId && !props.route.workerId) {
        setPreview(null);
        setHistory([]);
        setPreviousPreview(null);
        setError(null);
        return;
      }

      try {
        const nextPreview = props.route.runId
          ? await getPreview(props.route.runId)
          : await createPreview(props.route.workerId ?? '');

        const historyResponse = await getPreviewHistory(nextPreview.workerId);
        const nextPrevious = nextPreview.previousRunId ? await getPreview(nextPreview.previousRunId) : null;
        if (!cancelled) {
          setPreview(nextPreview);
          setHistory(historyResponse.previews);
          setPreviousPreview(nextPrevious);
          setError(nextPreview.errorMessage);
        }
      } catch (loadError) {
        if (!cancelled) {
          setPreview(null);
          setHistory([]);
          setPreviousPreview(null);
          setError(loadError instanceof Error ? loadError.message : 'Failed to load preview.');
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [props.route.runId, props.route.workerId]);

  const visibleDiffRows = useMemo(
    () => (props.route.showAllAttributes ? preview?.diffRows ?? [] : (preview?.diffRows ?? []).filter((row) => row.changed)),
    [preview, props.route.showAllAttributes],
  );
  const riskCallouts = useMemo(() => (preview ? buildRiskCallouts(preview) : []), [preview]);

  return (
    <>
      <section className="hero hero-compact">
        <div>
          <p className="eyebrow">Staging Sync</p>
          <h1>Worker Preview</h1>
          <p className="lede">Enter a worker ID to fetch the worker from SuccessFactors, compare it to Active Directory, and inspect the staged operations before a real sync.</p>
        </div>
      </section>

      <section className="panel">
        <form
          className="filters preview-form"
          onSubmit={(event) => {
            event.preventDefault();
            const params = new URLSearchParams();
            if (lookupWorkerId.trim()) {
              params.set('workerId', lookupWorkerId.trim());
            }
            if (props.route.showAllAttributes) {
              params.set('showAllAttributes', 'true');
            }
            props.onNavigate(`/preview${params.toString() ? `?${params.toString()}` : ''}`);
          }}
        >
          <label className="filter-search">
            <span>Worker Id</span>
            <input value={lookupWorkerId} onChange={(event) => setLookupWorkerId(event.target.value)} placeholder="1000123" />
          </label>
          <label className="filter-check">
            <span>Show all attributes</span>
            <input
              type="checkbox"
              checked={props.route.showAllAttributes}
              onChange={(event) => {
                const params = new URLSearchParams();
                if (props.route.runId) {
                  params.set('runId', props.route.runId);
                }
                if (!props.route.runId && lookupWorkerId.trim()) {
                  params.set('workerId', lookupWorkerId.trim());
                }
                if (event.target.checked) {
                  params.set('showAllAttributes', 'true');
                }
                props.onNavigate(`/preview${params.toString() ? `?${params.toString()}` : ''}`);
              }}
            />
          </label>
          <div className="filter-actions">
            <button type="submit">Preview Worker</button>
          </div>
        </form>
      </section>

      {error ? <DiagnosticsSection message={error} tone="danger" /> : null}

      {applyResult ? (
        <DiagnosticsSection message={applyResult.message} tone={applyResult.succeeded ? 'good' : 'danger'}>
          <ApplyResultDetails result={applyResult} />
        </DiagnosticsSection>
      ) : null}

      {preview ? (
        <>
          <section className="panel-grid preview-summary-grid">
            <article className="panel">
              <div className="section-heading">
                <div>
                  <h2>Decision Summary</h2>
                  <p className="muted">This preview was persisted as {preview.runId ? <button type="button" className="inline-link" onClick={() => props.onOpenRun(preview.runId!)}>{preview.runId}</button> : 'n/a'}.</p>
                </div>
                <div className="decision-badges">
                  {preview.buckets.map((bucket) => <span key={bucket} className="bucket-card">{bucket}</span>)}
                </div>
              </div>
              <dl className="kv">
                <div><dt>Action</dt><dd>{preview.operationSummary?.action ?? 'No operation'}</dd></div>
                <div><dt>Changed Attributes</dt><dd>{preview.diffRows.filter((row) => row.changed).length}</dd></div>
                <div><dt>Target OU</dt><dd>{preview.targetOu ?? 'n/a'}</dd></div>
                <div><dt>Manager</dt><dd>{preview.managerDistinguishedName ?? 'Unresolved'}</dd></div>
                <div><dt>Enable State</dt><dd>{enableTransition(preview.currentEnabled, preview.proposedEnable)}</dd></div>
                <div><dt>Matched Existing User</dt><dd>{displayBool(preview.matchedExistingUser)}</dd></div>
                <div><dt>SAM</dt><dd>{preview.samAccountName ?? 'n/a'}</dd></div>
                <div><dt>Current DN</dt><dd>{preview.currentDistinguishedName ?? 'n/a'}</dd></div>
              </dl>
              {preview.operationSummary?.effect ? <p className="callout">{preview.operationSummary.effect}</p> : null}
              {preview.reason || preview.reviewCaseType ? (
                <dl className="kv preview-meta">
                  <div><dt>Reason</dt><dd>{preview.reason ?? 'n/a'}</dd></div>
                  <div><dt>Review Case</dt><dd>{preview.reviewCaseType ?? 'n/a'}</dd></div>
                </dl>
              ) : null}
            </article>

            <article className="panel">
              <h2>Apply Guardrail</h2>
              <p className="muted">Applying uses the saved preview snapshot, not a silent re-run. This action writes to real Active Directory.</p>
              {!riskCallouts.length ? (
                <p className="callout good">No high-risk fields changed in this preview.</p>
              ) : (
                <div className="risk-list">
                  {riskCallouts.map((callout) => <p key={callout} className="callout warn">{callout}</p>)}
                </div>
              )}
              <form
                className="apply-preview-form"
                onSubmit={async (event) => {
                  event.preventDefault();
                  if (!preview.runId) {
                    return;
                  }
                  try {
                    const result = await applyPreview(preview.workerId, preview.runId, preview.fingerprint, acknowledgeRealSync);
                    setApplyResult(result);
                    props.onFlash({ tone: result.succeeded ? 'good' : 'danger', message: result.message });
                  } catch (applyError) {
                    setError(applyError instanceof Error ? applyError.message : 'Failed to apply preview.');
                  }
                }}
              >
                <label className="filter-check">
                  <span>I understand this will perform a real sync to AD using the saved preview.</span>
                  <input type="checkbox" checked={acknowledgeRealSync} onChange={(event) => setAcknowledgeRealSync(event.target.checked)} />
                </label>
                <p className="callout warn">Real sync uses the reviewed preview fingerprint shown in this page and will fail if the saved snapshot is stale.</p>
                <div className="filter-actions">
                  <button type="submit" disabled={!acknowledgeRealSync}>Real Sync To AD</button>
                </div>
              </form>
            </article>
          </section>

          {previousPreview ? (
            <section className="panel">
              <h2>Previous Preview Comparison</h2>
              <p className="muted">Comparing this saved preview to {previousPreview.runId ? <button type="button" className="inline-link" onClick={() => props.onOpenRun(previousPreview.runId!)}>{previousPreview.runId}</button> : 'n/a'}.</p>
              <dl className="kv preview-meta">
                <div><dt>Previous Action</dt><dd>{previousPreview.operationSummary?.action ?? 'Unknown'}</dd></div>
                <div><dt>Previous Changes</dt><dd>{previousPreview.diffRows.filter((row) => row.changed).length}</dd></div>
                <div><dt>Plan Changed</dt><dd>{preview.fingerprint === previousPreview.fingerprint ? 'No' : 'Yes'}</dd></div>
              </dl>
              {preview.fingerprint !== previousPreview.fingerprint ? <p className="callout warn">The planned action or changed attributes differ from the previous saved preview for this worker.</p> : null}
            </section>
          ) : null}

          {history.length > 1 ? (
            <section className="panel">
              <h2>Preview History</h2>
              <div className="table-scroll">
                <table className="data-table compact">
                  <thead>
                    <tr>
                      <th>Started</th>
                      <th>Run</th>
                      <th>Action</th>
                      <th>Status</th>
                      <th>Changes</th>
                      <th>Reason</th>
                    </tr>
                  </thead>
                  <tbody>
                    {history.map((item) => (
                      <tr key={item.runId}>
                        <td>{formatTimestamp(item.startedAt)}</td>
                        <td><button type="button" className="inline-link" onClick={() => props.onNavigate(`/preview?runId=${encodeURIComponent(item.runId)}`)}>{item.runId}</button></td>
                        <td>{item.action ?? item.bucket}</td>
                        <td>{item.status ?? 'Unknown'}</td>
                        <td>{item.changeCount}</td>
                        <td>{item.reason ?? 'n/a'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          ) : null}

          <section className="panel">
            <div className="section-heading">
              <div>
                <h2>Attribute Diff</h2>
                <p className="muted">{props.route.showAllAttributes ? 'Showing every synced attribute.' : 'Showing changed attributes only.'}</p>
              </div>
              <div className="diff-toggle">
                <button
                  type="button"
                  className="inline-link"
                  onClick={() => {
                    const params = new URLSearchParams();
                    if (preview.runId) {
                      params.set('runId', preview.runId);
                    } else {
                      params.set('workerId', preview.workerId);
                    }
                    if (!props.route.showAllAttributes) {
                      params.set('showAllAttributes', 'true');
                    }
                    props.onNavigate(`/preview?${params.toString()}`);
                  }}
                >
                  {props.route.showAllAttributes ? 'Show changed only' : 'Show all attributes'}
                </button>
              </div>
            </div>
            {!visibleDiffRows.length ? (
              <p className="muted">No synced attributes were recorded for this preview.</p>
            ) : (
              <div className="table-scroll preview-diff-scroll">
                <table className="data-table preview-diff-table">
                  <thead>
                    <tr>
                      <th>Attribute</th>
                      <th>Source</th>
                      <th>Status</th>
                      <th>Current</th>
                      <th>Proposed</th>
                    </tr>
                  </thead>
                  <tbody>
                    {visibleDiffRows.map((row) => (
                      <tr key={`${row.attribute}-${row.source ?? 'none'}`}>
                        <td>{row.attribute}</td>
                        <td>{row.source ?? 'n/a'}</td>
                        <td>{row.changed ? 'Changed' : 'Same'}</td>
                        <td>{row.before}</td>
                        <td>{row.after}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>

          <SourceConfidenceSections preview={preview} />
        </>
      ) : null}
    </>
  );
}

function RunDetailPage(props: { route: Extract<Route, { kind: 'run' }>; onNavigate: (path: string) => void }) {
  const [run, setRun] = useState<RunDetail | null>(null);
  const [response, setResponse] = useState<RunEntriesResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [workerIdInput, setWorkerIdInput] = useState(props.route.workerId);
  const [filterInput, setFilterInput] = useState(props.route.filter);

  useEffect(() => {
    setWorkerIdInput(props.route.workerId);
    setFilterInput(props.route.filter);
  }, [props.route.workerId, props.route.filter]);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const [runDetail, entriesResponse] = await Promise.all([
          getRun(props.route.runId),
          getRunEntries(props.route.runId, {
            bucket: props.route.bucket || undefined,
            workerId: props.route.workerId || undefined,
            filter: props.route.filter || undefined,
            page: props.route.page,
            pageSize: ENTRY_PAGE_SIZE,
          }),
        ]);
        if (!cancelled) {
          setRun(runDetail);
          setResponse(entriesResponse);
          setError(null);
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
  }, [props.route]);

  const availableBuckets = Object.entries(run?.bucketCounts ?? {}).filter(([, count]) => count > 0);
  const totalPages = Math.max(1, Math.ceil((response?.total ?? 0) / ENTRY_PAGE_SIZE));

  return (
    <>
      {error ? <section className="panel"><p className="callout danger">{error}</p></section> : null}
      {!run ? null : (
        <>
          <section className="hero hero-compact">
            <div>
              <p className="eyebrow">Run Detail</p>
              <h1>{run.run.runId}</h1>
              <p className="lede">{run.run.mode} · {run.run.syncScope} · {run.run.status} · {run.run.dryRun ? 'Dry Run' : 'Live Run'}</p>
            </div>
          </section>

          <section className="panel-grid">
            <article className="panel">
              <h2>Summary</h2>
              <dl className="kv">
                <div><dt>Started</dt><dd>{formatTimestamp(run.run.startedAt)}</dd></div>
                <div><dt>Completed</dt><dd>{formatTimestamp(run.run.completedAt)}</dd></div>
                <div><dt>Artifact</dt><dd>{run.run.artifactType}</dd></div>
                <div><dt>Scope</dt><dd>{run.run.syncScope}</dd></div>
                <div><dt>Trigger</dt><dd>{run.run.runTrigger}</dd></div>
                <div><dt>Requested By</dt><dd>{run.run.requestedBy ?? 'n/a'}</dd></div>
                <div><dt>Status</dt><dd><span className={`badge ${badgeClass(run.run.status)}`}>{run.run.status}</span></dd></div>
                <div><dt>Processed</dt><dd>{run.run.processedWorkers}</dd></div>
                <div><dt>Total</dt><dd>{run.run.totalWorkers}</dd></div>
              </dl>
            </article>

            <article className="panel">
              <h2>Bucket Counts</h2>
              <div className="bucket-grid">
                {Object.entries(run.bucketCounts).map(([bucket, count]) => (
                  <button key={bucket} type="button" className="bucket-card" onClick={() => props.onNavigate(buildRunPath(props.route.runId, { ...props.route, bucket, page: 1 }))}>
                    <span className="bucket-name">{bucket}</span>
                    <strong>{count}</strong>
                  </button>
                ))}
              </div>
            </article>
          </section>

          <section className="panel">
            <h2>Filters</h2>
            <form
              className="filters"
              onSubmit={(event) => {
                event.preventDefault();
                props.onNavigate(buildRunPath(props.route.runId, { ...props.route, workerId: workerIdInput, filter: filterInput, page: 1 }));
              }}
            >
              <label>
                <span>Worker search</span>
                <input value={workerIdInput} onChange={(event) => setWorkerIdInput(event.target.value)} placeholder="worker id or samAccountName" />
              </label>
              <label className="filter-search">
                <span>Text search</span>
                <input value={filterInput} onChange={(event) => setFilterInput(event.target.value)} placeholder="reason, reviewCaseType, raw entry text..." />
              </label>
              <div className="filter-actions">
                <button type="submit">Apply</button>
                <button type="button" className="link-button" onClick={() => props.onNavigate(`/runs/${encodeURIComponent(props.route.runId)}`)}>Clear</button>
              </div>
            </form>
            <div className="bucket-chip-row">
              <button type="button" className={`bucket-card ${props.route.bucket ? '' : 'active'}`} onClick={() => props.onNavigate(buildRunPath(props.route.runId, { ...props.route, bucket: '', page: 1 }))}>All buckets</button>
              {availableBuckets.map(([bucket, count]) => (
                <button
                  key={bucket}
                  type="button"
                  className={`bucket-card ${props.route.bucket === bucket ? 'active' : ''}`}
                  onClick={() => props.onNavigate(buildRunPath(props.route.runId, { ...props.route, bucket, page: 1 }))}
                >
                  <span className="bucket-name">{bucket}</span>
                  <strong>{count}</strong>
                </button>
              ))}
            </div>
          </section>

          <section className="panel">
            <h2>Entries {props.route.bucket ? `· ${props.route.bucket}` : ''}</h2>
            <p className="muted">Showing page {props.route.page} of {totalPages} · {response?.total ?? 0} total entries · {ENTRY_PAGE_SIZE} per page.</p>
            {!response?.entries.length ? (
              <p className="muted">No entries matched this run.</p>
            ) : (
              <div className="entry-list">
                {response.entries.map((entry) => {
                  const savedPreviewRunId = getSavedPreviewRunId(entry);
                  return (
                    <article className="entry-card" key={entry.entryId}>
                      <header className="entry-head">
                        <div>
                          <h3>{entry.bucketLabel}</h3>
                          <p className="muted">{entry.workerId ?? 'n/a'} / {entry.samAccountName ?? 'n/a'}</p>
                        </div>
                        <div className="entry-meta">
                          <span className="badge neutral">{entry.changeCount} changes</span>
                        </div>
                      </header>
                      {entry.primarySummary ? <p className="entry-summary">{entry.primarySummary}</p> : null}
                      {entry.failureSummary ? <p className="callout warn">{entry.failureSummary}</p> : null}
                      {entry.reason || entry.reviewCaseType ? (
                        <>
                          <dl className="kv preview-meta">
                            <div><dt>Reason</dt><dd>{entry.reason ?? 'n/a'}</dd></div>
                            <div><dt>Review Case</dt><dd>{entry.reviewCaseType ?? 'n/a'}</dd></div>
                          </dl>
                          <RunEntryDiagnostics entry={entry} />
                        </>
                      ) : null}
                      {entry.operationSummary ? <p><strong>{entry.operationSummary.action}</strong>{entry.operationSummary.effect ? ` · ${entry.operationSummary.effect}` : ''}</p> : null}
                      {entry.topChangedAttributes.length ? <p className="muted">Top changes: {entry.topChangedAttributes.join(', ')}</p> : null}
                      {entry.workerId ? (
                        <p>
                          <button
                            type="button"
                            className="inline-link"
                            onClick={() => props.onNavigate(savedPreviewRunId ? `/preview?runId=${encodeURIComponent(savedPreviewRunId)}` : `/preview?workerId=${encodeURIComponent(entry.workerId!)}`)}
                          >
                            {savedPreviewRunId ? 'Open saved preview' : 'Open worker preview'}
                          </button>
                        </p>
                      ) : null}
                      {entry.diffRows.length ? (
                        <table className="data-table compact">
                          <thead>
                            <tr>
                              <th>Attribute</th>
                              <th>Before</th>
                              <th>After</th>
                            </tr>
                          </thead>
                          <tbody>
                            {entry.diffRows.map((diff) => (
                              <tr key={`${entry.entryId}-${diff.attribute}`}>
                                <td>{diff.attribute}</td>
                                <td>{diff.before}</td>
                                <td>{diff.after}</td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      ) : <p className="muted">No detailed attribute diff was recorded for this entry.</p>}
                    </article>
                  );
                })}
              </div>
            )}

            {totalPages > 1 ? (
              <div className="filter-actions">
                {props.route.page > 1 ? <button type="button" className="link-button" onClick={() => props.onNavigate(buildRunPath(props.route.runId, { ...props.route, page: props.route.page - 1 }))}>Previous</button> : null}
                {props.route.page < totalPages ? <button type="button" className="link-button" onClick={() => props.onNavigate(buildRunPath(props.route.runId, { ...props.route, page: props.route.page + 1 }))}>Next</button> : null}
              </div>
            ) : null}
          </section>
        </>
      )}
    </>
  );
}

function UsersPage(props: { currentUserId: string | null; onFlash: (flash: Flash) => void }) {
  const [users, setUsers] = useState<LocalUserSummary[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [createUsername, setCreateUsername] = useState('');
  const [createPassword, setCreatePassword] = useState('');
  const [createPasswordConfirmation, setCreatePasswordConfirmation] = useState('');
  const [createIsAdmin, setCreateIsAdmin] = useState(false);
  const [resetPasswords, setResetPasswords] = useState<Record<string, { password: string; confirmation: string }>>({});

  async function refresh() {
    const response = await listUsers();
    setUsers(response.users);
  }

  useEffect(() => {
    void refresh().catch((loadError) => {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load users.');
    });
  }, []);

  return (
    <>
      <section className="hero hero-compact">
        <div>
          <p className="eyebrow">Administration</p>
          <h1>User Access</h1>
          <p className="lede">Create operator accounts, promote admins, reset passwords, and disable or delete local logins.</p>
        </div>
      </section>

      {error ? <section className="panel"><p className="callout danger">{error}</p></section> : null}

      <section className="panel-grid admin-user-grid">
        <article className="panel">
          <h2>Create User</h2>
          <form
            className="filters admin-user-form"
            onSubmit={async (event) => {
              event.preventDefault();
              if (createPassword !== createPasswordConfirmation) {
                setError('Create password and confirmation must match.');
                return;
              }

              try {
                const result = await createUser(createUsername, createPassword, createIsAdmin);
                props.onFlash({ tone: result.succeeded ? 'good' : 'danger', message: result.message });
                setCreateUsername('');
                setCreatePassword('');
                setCreatePasswordConfirmation('');
                setCreateIsAdmin(false);
                setError(null);
                await refresh();
              } catch (createError) {
                setError(createError instanceof Error ? createError.message : 'Failed to create user.');
              }
            }}
          >
            <label>
              <span>Username</span>
              <input value={createUsername} onChange={(event) => setCreateUsername(event.target.value)} autoComplete="off" />
            </label>
            <label>
              <span>Password</span>
              <input type="password" value={createPassword} onChange={(event) => setCreatePassword(event.target.value)} autoComplete="new-password" />
            </label>
            <label>
              <span>Confirm password</span>
              <input type="password" value={createPasswordConfirmation} onChange={(event) => setCreatePasswordConfirmation(event.target.value)} autoComplete="new-password" />
            </label>
            <label className="filter-check">
              <span>Create as admin</span>
              <input type="checkbox" checked={createIsAdmin} onChange={(event) => setCreateIsAdmin(event.target.checked)} />
            </label>
            <div className="filter-actions">
              <button type="submit">Create user</button>
            </div>
          </form>
        </article>

        <article className="panel">
          <h2>Security Notes</h2>
          <div className="risk-list">
            <p className="callout">Passwords are stored as one-way hashes.</p>
            <p className="callout">New passwords must be at least 12 characters and include uppercase, lowercase, and numeric characters.</p>
            <p className="callout warn">This page cannot disable or delete your own account, and it will not remove the last active admin.</p>
          </div>
        </article>
      </section>

      <section className="panel">
        <div className="section-heading">
          <div>
            <p className="eyebrow">Directory</p>
            <h2>Local Users</h2>
          </div>
        </div>
        <div className="table-scroll">
          <table className="data-table admin-user-table">
            <thead>
              <tr>
                <th>Username</th>
                <th>Role</th>
                <th>Status</th>
                <th>Created</th>
                <th>Last Login</th>
                <th>Reset Password</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {users.map((user) => {
                const resetState = resetPasswords[user.userId] ?? { password: '', confirmation: '' };
                return (
                  <tr key={user.userId}>
                    <td>{user.username}</td>
                    <td>
                      <div className="admin-user-role-form">
                        <span className={`badge ${user.role.toLowerCase() === 'admin' ? 'info' : 'neutral'}`}>{user.role}</span>
                        <button
                          type="button"
                          className="link-button admin-user-compact-button"
                          onClick={async () => {
                            try {
                              const result = await setUserRole(user.userId, user.role.toLowerCase() !== 'admin');
                              props.onFlash({ tone: result.succeeded ? 'good' : 'danger', message: result.message });
                              await refresh();
                            } catch (roleError) {
                              setError(roleError instanceof Error ? roleError.message : 'Failed to change role.');
                            }
                          }}
                        >
                          {user.role.toLowerCase() === 'admin' ? 'Set regular' : 'Set admin'}
                        </button>
                      </div>
                    </td>
                    <td><span className={`badge ${user.isActive ? 'good' : 'dim'}`}>{user.isActive ? 'Active' : 'Inactive'}</span></td>
                    <td>{formatTimestamp(user.createdAt)}</td>
                    <td>{formatTimestamp(user.lastLoginAt)}</td>
                    <td>
                      <div className="admin-user-inline-form">
                        <input
                          type="password"
                          value={resetState.password}
                          onChange={(event) => setResetPasswords((current) => ({ ...current, [user.userId]: { ...resetState, password: event.target.value } }))}
                          placeholder="New"
                          autoComplete="new-password"
                        />
                        <input
                          type="password"
                          value={resetState.confirmation}
                          onChange={(event) => setResetPasswords((current) => ({ ...current, [user.userId]: { ...resetState, confirmation: event.target.value } }))}
                          placeholder="Confirm"
                          autoComplete="new-password"
                        />
                        <button
                          type="button"
                          className="admin-user-compact-button"
                          onClick={async () => {
                            if (resetState.password !== resetState.confirmation) {
                              setError('Reset password and confirmation must match.');
                              return;
                            }
                            try {
                              const result = await resetUserPassword(user.userId, resetState.password);
                              props.onFlash({ tone: result.succeeded ? 'good' : 'danger', message: result.message });
                              setResetPasswords((current) => ({ ...current, [user.userId]: { password: '', confirmation: '' } }));
                            } catch (passwordError) {
                              setError(passwordError instanceof Error ? passwordError.message : 'Failed to reset password.');
                            }
                          }}
                        >
                          Reset
                        </button>
                      </div>
                    </td>
                    <td>
                      <div className="admin-user-actions">
                        <button
                          type="button"
                          className="link-button admin-user-compact-button"
                          onClick={async () => {
                            try {
                              const result = await setUserActive(user.userId, !user.isActive);
                              props.onFlash({ tone: result.succeeded ? 'good' : 'danger', message: result.message });
                              await refresh();
                            } catch (activeError) {
                              setError(activeError instanceof Error ? activeError.message : 'Failed to update user state.');
                            }
                          }}
                        >
                          {user.isActive ? 'Disable' : 'Enable'}
                        </button>
                        <button
                          type="button"
                          className="link-button danger-button admin-user-compact-button"
                          disabled={props.currentUserId === user.userId}
                          onClick={async () => {
                            try {
                              const result = await deleteUser(user.userId);
                              props.onFlash({ tone: result.succeeded ? 'good' : 'danger', message: result.message });
                              await refresh();
                            } catch (deleteError) {
                              setError(deleteError instanceof Error ? deleteError.message : 'Failed to delete user.');
                            }
                          }}
                        >
                          Delete
                        </button>
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>
    </>
  );
}
