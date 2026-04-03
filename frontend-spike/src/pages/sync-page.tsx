import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  AlertTriangle,
  ArrowRight,
  CalendarClock,
  Clock3,
  RefreshCcw,
  ShieldAlert,
  Trash2,
} from 'lucide-react'

import { api } from '@/lib/api'
import { formatDate, statusTone } from '@/lib/format'
import type { DashboardSnapshot, RunQueueRequest, RunSummary, SyncScheduleStatus } from '@/lib/types'

type SyncState = {
  dashboard: DashboardSnapshot | null
  queue: RunQueueRequest | null
  schedule: SyncScheduleStatus | null
  loading: boolean
  error: string | null
  success: string | null
}

const initialState: SyncState = {
  dashboard: null,
  queue: null,
  schedule: null,
  loading: true,
  error: null,
  success: null,
}

function progressPercent(processedWorkers: number, totalWorkers: number) {
  if (totalWorkers <= 0) return 0
  return Math.max(0, Math.min(100, Math.round((processedWorkers / totalWorkers) * 100)))
}

export function SyncPage() {
  const [state, setState] = useState(initialState)
  const [runMode, setRunMode] = useState<'DryRun' | 'LiveRun'>('DryRun')
  const [scheduleEnabled, setScheduleEnabled] = useState(false)
  const [intervalMinutes, setIntervalMinutes] = useState(30)
  const [deleteConfirmOpen, setDeleteConfirmOpen] = useState(false)
  const [deleteConfirmationText, setDeleteConfirmationText] = useState('')

  useEffect(() => {
    let mounted = true

    async function load() {
      try {
        if (mounted) {
          setState((current) => ({ ...current, loading: true }))
        }

        const [dashboard, queue, schedule] = await Promise.all([api.dashboard(), api.queue(), api.schedule()])

        if (mounted) {
          setState((current) => ({
            ...current,
            dashboard,
            queue: queue.request,
            schedule: schedule.schedule,
            loading: false,
          }))
          setScheduleEnabled(schedule.schedule.enabled)
          setIntervalMinutes(schedule.schedule.intervalMinutes)
        }
      } catch (error) {
        if (mounted) {
          setState((current) => ({
            ...current,
            loading: false,
            error: error instanceof Error ? error.message : 'Unable to load sync page.',
          }))
        }
      }
    }

    void load()
    return () => {
      mounted = false
    }
  }, [])

  const runs = useMemo(() => state.dashboard?.runs ?? [], [state.dashboard])
  const activeRun = state.dashboard?.activeRun ?? null
  const status = state.dashboard?.status
  const hasPendingOrActiveRun = state.queue != null || activeRun != null
  const progress = progressPercent(status?.processedWorkers ?? 0, status?.totalWorkers ?? 0)

  async function refreshAfter(message?: { success?: string; error?: string }) {
    const [dashboard, queue, schedule] = await Promise.all([api.dashboard(), api.queue(), api.schedule()])

    setState((current) => ({
      ...current,
      dashboard,
      queue: queue.request,
      schedule: schedule.schedule,
      success: message?.success ?? null,
      error: message?.error ?? null,
      loading: false,
    }))
    setScheduleEnabled(schedule.schedule.enabled)
    setIntervalMinutes(schedule.schedule.intervalMinutes)
  }

  async function handleQueueRun() {
    try {
      await api.createRun({
        dryRun: runMode !== 'LiveRun',
        mode: 'BulkSync',
        runTrigger: 'AdHoc',
      })

      await refreshAfter({
        success: runMode === 'LiveRun' ? 'Live provisioning run queued.' : 'Dry-run sync queued.',
      })
    } catch (error) {
      setState((current) => ({
        ...current,
        error: error instanceof Error ? error.message : 'Unable to queue run.',
      }))
    }
  }

  async function handleCancelRun() {
    try {
      await api.cancelRun()
      await refreshAfter({ success: 'Run cancellation requested.' })
    } catch (error) {
      setState((current) => ({
        ...current,
        error: error instanceof Error ? error.message : 'Unable to cancel run.',
      }))
    }
  }

  async function handleSaveSchedule() {
    try {
      await api.updateSchedule({
        enabled: scheduleEnabled,
        intervalMinutes,
      })

      await refreshAfter({
        success: scheduleEnabled ? `Recurring sync enabled every ${intervalMinutes} minutes.` : 'Recurring sync disabled.',
      })
    } catch (error) {
      setState((current) => ({
        ...current,
        error: error instanceof Error ? error.message : 'Unable to save schedule.',
      }))
    }
  }

  async function handleDeleteAllUsers() {
    try {
      await api.createRun({
        dryRun: false,
        mode: 'DeleteAllUsers',
        runTrigger: 'DeleteAllUsers',
      })
      setDeleteConfirmOpen(false)
      setDeleteConfirmationText('')
      await refreshAfter({ success: 'Delete-all test run queued.' })
    } catch (error) {
      setState((current) => ({
        ...current,
        error: error instanceof Error ? error.message : 'Unable to queue delete-all run.',
      }))
    }
  }

  return (
    <>
      <section className="vf-hero vf-hero-compact">
        <div className="max-w-[54rem]">
          <p className="vf-eyebrow vf-eyebrow-dark">Provisioning Control</p>
          <h1 className="vf-hero-title">Sync</h1>
          <p className="vf-hero-lede">
            Queue ad hoc provisioning runs, monitor the current worker state, and manage the recurring
            full-sync schedule from one control surface.
          </p>
        </div>
        <div className="w-full max-w-[360px] rounded-[28px] border border-white/12 bg-white/8 p-5 shadow-[0_18px_40px_rgba(0,0,0,0.18)] backdrop-blur-[18px]">
          <p className="vf-eyebrow vf-eyebrow-dark">Current pulse</p>
          <p className="mt-2 text-[28px] font-semibold tracking-[-0.03em] text-white">
            {status?.status ?? 'Unknown'}
          </p>
          <div className="vf-runtime-progress mt-5">
            <div className="vf-progress-row">
              <span>Run progress</span>
              <strong className="text-white">{progress}%</strong>
            </div>
            <div className="vf-progress-track">
              <span className="vf-progress-bar" style={{ width: `${progress}%` }} />
            </div>
            <p className="text-[14px] leading-[1.43] tracking-[-0.016em] text-white/72">
              {status ? `${status.processedWorkers} of ${status.totalWorkers} workers processed` : 'No active worker progress.'}
            </p>
          </div>
        </div>
      </section>

      <section className="vf-summary-grid">
        <article className="vf-stat-card">
          <p className="vf-stat-label">Current run</p>
          <p className="vf-stat-value">{activeRun?.runId ?? state.queue?.runId ?? 'Idle'}</p>
          <p className="vf-stat-meta">{activeRun ? `${activeRun.mode} · ${activeRun.dryRun ? 'Dry run' : 'Live run'}` : 'No active run currently executing'}</p>
        </article>
        <article className="vf-stat-card">
          <p className="vf-stat-label">Queued state</p>
          <p className="vf-stat-value">{state.queue?.status ?? 'None'}</p>
          <p className="vf-stat-meta">{state.queue ? `${state.queue.mode} requested by ${state.queue.requestedBy ?? 'n/a'}` : 'No pending queued request'}</p>
        </article>
        <article className="vf-stat-card">
          <p className="vf-stat-label">Schedule</p>
          <p className="vf-stat-value">{state.schedule?.enabled ? 'Enabled' : 'Paused'}</p>
          <p className="vf-stat-meta">{state.schedule ? `Next run ${formatDate(state.schedule.nextRunAt ?? null)}` : 'Schedule has not been loaded yet'}</p>
        </article>
      </section>

      {state.error ? (
        <section className="vf-panel">
          <p className="vf-callout vf-callout-danger">{state.error}</p>
        </section>
      ) : null}

      {state.success ? (
        <section className="vf-panel">
          <p className="vf-callout vf-callout-good">{state.success}</p>
        </section>
      ) : null}

      {state.loading && !state.dashboard ? (
        <section className="vf-panel">
          <div className="vf-skeleton-panel">
            <div className="vf-skeleton-line" data-width="lg" />
            <div className="vf-skeleton-line" data-width="md" />
            <div className="vf-skeleton-line" />
            <div className="vf-skeleton-line" />
          </div>
        </section>
      ) : null}

      <section className="vf-sync-control-grid">
        <div className="vf-section-stack">
          <article className="vf-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">Live status</p>
                <h2 className="vf-panel-title">Current run</h2>
              </div>
              <span className={`vf-badge ${statusTone(status?.status ?? 'unknown')}`}>{status?.status ?? 'Unknown'}</span>
            </div>

            <div className="vf-run-stats mt-5">
              <div className="vf-run-stat-row">
                <strong>Stage</strong>
                <span>{status?.stage ?? 'Unknown'}</span>
              </div>
              <div className="vf-run-stat-row">
                <strong>Run Id</strong>
                <span>{status?.runId ?? 'None'}</span>
              </div>
              <div className="vf-run-stat-row">
                <strong>Trigger</strong>
                <span>{activeRun?.runTrigger ?? 'n/a'}</span>
              </div>
              <div className="vf-run-stat-row">
                <strong>Requested By</strong>
                <span>{activeRun?.requestedBy ?? state.queue?.requestedBy ?? 'n/a'}</span>
              </div>
              <div className="vf-run-stat-row">
                <strong>Last Action</strong>
                <span>{status?.lastAction ?? 'None'}</span>
              </div>
              <div className="vf-run-stat-row">
                <strong>Last Updated</strong>
                <span>{formatDate(status?.lastUpdatedAt ?? null)}</span>
              </div>
            </div>

            {activeRun ? (
              <div className="vf-command-actions mt-5">
                <Link className="vf-inline-link" to={`/runs/${activeRun.runId}`}>
                  Open active run <ArrowRight className="ml-1 inline size-4" />
                </Link>
              </div>
            ) : null}
          </article>

          <article className="vf-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">Run control</p>
                <h2 className="vf-panel-title">Ad hoc run</h2>
              </div>
              <RefreshCcw className={`size-5 text-[color:var(--vf-ink-faint)] ${state.loading ? 'animate-spin' : ''}`} />
            </div>

            <p className="vf-muted-text mt-3">
              Full-source sync only. Dry-run is the default and safest option.
            </p>

            {hasPendingOrActiveRun ? (
              <p className="vf-callout vf-callout-warn mt-5">
                A run is already pending or active. You can request cancellation and the worker will stop gracefully.
              </p>
            ) : null}

            <div className="vf-command-card mt-5">
              <label>
                <span>Run type</span>
                <select className="vf-select mt-2" disabled={hasPendingOrActiveRun} value={runMode} onChange={(event) => setRunMode(event.target.value as 'DryRun' | 'LiveRun')}>
                  <option value="DryRun">Dry run</option>
                  <option value="LiveRun">Live provisioning</option>
                </select>
              </label>

              <div className="vf-command-actions">
                <button className="vf-primary-button" disabled={state.loading} type="button" onClick={hasPendingOrActiveRun ? handleCancelRun : handleQueueRun}>
                  {state.queue?.status === 'CancelRequested' ? 'Cancel requested' : hasPendingOrActiveRun ? 'Cancel run' : 'Queue run'}
                </button>
                <p className="vf-muted-text">
                  {runMode === 'LiveRun' ? 'Queues a live provisioning run.' : 'Queues a dry run with no AD writes.'}
                </p>
              </div>
            </div>
          </article>

          <article className="vf-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">History</p>
                <h2 className="vf-panel-title">Recent runs</h2>
              </div>
              <Link className="vf-inline-link" to="/">
                Back to dashboard <ArrowRight className="ml-1 inline size-4" />
              </Link>
            </div>

            {runs.length === 0 ? (
              <div className="vf-empty-state">
                {state.loading ? 'Loading runs...' : 'No runs have been recorded yet.'}
              </div>
            ) : (
              <div className="vf-list mt-5">
                {runs.slice(0, 6).map((run: RunSummary) => (
                  <article key={run.runId} className="vf-list-row">
                    <div>
                      <p className="vf-list-title">{run.runId}</p>
                      <p className="vf-list-meta">
                        {formatDate(run.startedAt)} · {run.runTrigger} · {run.syncScope}
                      </p>
                      <p className="vf-list-meta">
                        {run.dryRun ? 'Dry run' : 'Live run'} · {run.totalWorkers} workers
                      </p>
                    </div>
                    <div className="flex flex-col items-end gap-3">
                      <span className={`vf-badge ${statusTone(run.status)}`}>{run.status}</span>
                      <Link className="vf-inline-link" to={`/runs/${run.runId}`}>
                        Open
                      </Link>
                    </div>
                  </article>
                ))}
              </div>
            )}
          </article>
        </div>

        <div className="vf-section-stack">
          <article className="vf-panel vf-sticky-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">Automation</p>
                <h2 className="vf-panel-title">Recurring schedule</h2>
              </div>
              <CalendarClock className="size-5 text-[color:var(--vf-neutral)]" />
            </div>

            <div className="vf-run-stats mt-5">
              <div className="vf-run-stat-row">
                <strong>Enabled</strong>
                <span>{state.schedule?.enabled ? 'Yes' : 'No'}</span>
              </div>
              <div className="vf-run-stat-row">
                <strong>Interval</strong>
                <span>{state.schedule?.intervalMinutes ?? 30} minutes</span>
              </div>
              <div className="vf-run-stat-row">
                <strong>Next Run</strong>
                <span>{formatDate(state.schedule?.nextRunAt ?? null)}</span>
              </div>
              <div className="vf-run-stat-row">
                <strong>Last Scheduled</strong>
                <span>{formatDate(state.schedule?.lastScheduledRunAt ?? null)}</span>
              </div>
              <div className="vf-run-stat-row">
                <strong>Last Enqueue Attempt</strong>
                <span>{formatDate(state.schedule?.lastEnqueueAttemptAt ?? null)}</span>
              </div>
            </div>

            {state.schedule?.lastEnqueueError ? (
              <p className="vf-callout vf-callout-warn mt-5">{state.schedule.lastEnqueueError}</p>
            ) : null}

            <div className="vf-command-card mt-5">
              <label className="vf-checkbox-row">
                <span>Enabled</span>
                <input checked={scheduleEnabled} type="checkbox" onChange={(event) => setScheduleEnabled(event.target.checked)} />
              </label>
              <label>
                <span>Interval minutes</span>
                <input className="vf-input mt-2" max={1440} min={5} step={1} type="number" value={intervalMinutes} onChange={(event) => setIntervalMinutes(Number(event.target.value))} />
              </label>
              <div className="vf-command-actions">
                <button className="vf-primary-button" type="button" onClick={handleSaveSchedule}>
                  Save schedule
                </button>
                <p className="vf-muted-text">
                  {scheduleEnabled ? 'Recurring sync will run on the selected interval.' : 'Recurring sync is paused.'}
                </p>
              </div>
            </div>
          </article>

          <article className="vf-panel vf-danger-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">Danger zone</p>
                <h2 className="vf-panel-title">Testing reset</h2>
              </div>
              <ShieldAlert className="size-5 text-[color:var(--vf-bad)]" />
            </div>

            <p className="vf-muted-text mt-3">
              This queues a live delete job against all users currently returned by the worker source.
              Keep it isolated to controlled environments.
            </p>

            <div className="vf-command-card mt-5">
              <div className="vf-command-actions">
                <AlertTriangle className="size-5 text-[color:var(--vf-bad)]" />
                <p className="vf-muted-text">
                  Disabled while a run is already pending or active.
                </p>
              </div>
              <button className="vf-danger-button" disabled={hasPendingOrActiveRun} type="button" onClick={() => setDeleteConfirmOpen(true)}>
                <Trash2 className="size-4" />
                Testing Reset
              </button>
            </div>
          </article>

          <article className="vf-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">Timing</p>
                <h2 className="vf-panel-title">Operational cues</h2>
              </div>
              <Clock3 className="size-5 text-[color:var(--vf-ink-faint)]" />
            </div>

            <div className="vf-list mt-5">
              <article className="vf-list-row">
                <div>
                  <p className="vf-list-title">Queue state</p>
                  <p className="vf-list-meta">{state.queue ? `${state.queue.status} · requested ${formatDate(state.queue.requestedAt)}` : 'No queue request is waiting.'}</p>
                </div>
              </article>
              <article className="vf-list-row">
                <div>
                  <p className="vf-list-title">Current status update</p>
                  <p className="vf-list-meta">{formatDate(status?.lastUpdatedAt ?? null)}</p>
                </div>
              </article>
              <article className="vf-list-row">
                <div>
                  <p className="vf-list-title">Next scheduled action</p>
                  <p className="vf-list-meta">{formatDate(state.schedule?.nextRunAt ?? null)}</p>
                </div>
              </article>
            </div>
          </article>
        </div>
      </section>

      {deleteConfirmOpen ? (
        <section className="vf-dialog-backdrop">
          <div className="vf-dialog">
            <div className="vf-dialog-panel">
              <p className="vf-eyebrow">Danger Zone</p>
              <h2 className="vf-panel-title">Delete All Users</h2>
              <p className="vf-muted-text mt-4">
                Type <strong>DELETE ALL USERS</strong> to queue a live delete-all run against the current worker source.
              </p>
              <label className="block">
                <span>Confirmation text</span>
                <input autoFocus className="vf-input mt-2" type="text" value={deleteConfirmationText} onChange={(event) => setDeleteConfirmationText(event.target.value)} />
              </label>
              <div className="vf-filter-actions mt-5">
                <button className="vf-secondary-button" type="button" onClick={() => setDeleteConfirmOpen(false)}>
                  Cancel
                </button>
                <button className="vf-danger-button" disabled={deleteConfirmationText.trim() !== 'DELETE ALL USERS'} type="button" onClick={handleDeleteAllUsers}>
                  Queue delete-all run
                </button>
              </div>
            </div>
          </div>
        </section>
      ) : null}
    </>
  )
}
