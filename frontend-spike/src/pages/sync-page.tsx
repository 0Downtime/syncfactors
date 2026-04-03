import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'

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

        const [dashboard, queue, schedule] = await Promise.all([
          api.dashboard(),
          api.queue(),
          api.schedule(),
        ])

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

  async function refreshAfter(message?: { success?: string; error?: string }) {
    const [dashboard, queue, schedule] = await Promise.all([
      api.dashboard(),
      api.queue(),
      api.schedule(),
    ])

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
        success: scheduleEnabled
          ? `Recurring sync enabled every ${intervalMinutes} minutes.`
          : 'Recurring sync disabled.',
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
        <div>
          <p className="vf-eyebrow vf-eyebrow-dark">Provisioning Control</p>
          <h1 className="vf-hero-title">Sync</h1>
          <p className="vf-hero-lede">
            Queue ad hoc AD provisioning runs and manage the recurring full-sync schedule.
          </p>
        </div>
        <div className="flex flex-wrap gap-3">
          <button
            className="vf-danger-link"
            disabled={hasPendingOrActiveRun}
            type="button"
            onClick={() => setDeleteConfirmOpen(true)}
          >
            Testing Reset
          </button>
        </div>
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

      <section className="vf-panel-grid vf-sync-grid">
        <article className="vf-panel">
          <h2 className="vf-panel-title">Current Run</h2>
          <dl className="vf-kv">
            <div><dt>Status</dt><dd>{status?.status ?? 'Unknown'}</dd></div>
            <div><dt>Stage</dt><dd>{status?.stage ?? 'Unknown'}</dd></div>
            <div><dt>Run Id</dt><dd>{status?.runId ?? 'None'}</dd></div>
            <div><dt>Trigger</dt><dd>{activeRun?.runTrigger ?? 'n/a'}</dd></div>
            <div><dt>Requested By</dt><dd>{activeRun?.requestedBy ?? state.queue?.requestedBy ?? 'n/a'}</dd></div>
            <div><dt>Progress</dt><dd>{status ? `${status.processedWorkers} / ${status.totalWorkers}` : '0 / 0'}</dd></div>
            <div><dt>Last Action</dt><dd>{status?.lastAction ?? 'None'}</dd></div>
            <div><dt>Last Updated</dt><dd>{formatDate(status?.lastUpdatedAt ?? null)}</dd></div>
          </dl>
          {activeRun ? (
            <p className="mt-5">
              <Link className="vf-inline-link" to={`/runs/${activeRun.runId}`}>
                Open active run
              </Link>
            </p>
          ) : null}
        </article>

        <article className="vf-panel">
          <h2 className="vf-panel-title">Ad Hoc Run</h2>
          <p className="mt-4 text-[17px] leading-[1.47] tracking-[-0.022em] text-black/62">
            Full-source sync only. Dry-run is the default and safest option.
          </p>
          {hasPendingOrActiveRun ? (
            <p className="vf-callout vf-callout-warn mt-5">
              A run is already pending or active. You can request cancellation and the worker will stop gracefully.
            </p>
          ) : null}
          <div className="vf-form-grid mt-5">
            <label>
              <span>Run type</span>
              <select
                className="vf-select"
                disabled={hasPendingOrActiveRun}
                value={runMode}
                onChange={(event) => setRunMode(event.target.value as 'DryRun' | 'LiveRun')}
              >
                <option value="DryRun">Dry run</option>
                <option value="LiveRun">Live provisioning</option>
              </select>
            </label>
            <div className="vf-filter-actions">
              <button
                className="vf-primary-button"
                disabled={state.loading}
                type="button"
                onClick={hasPendingOrActiveRun ? handleCancelRun : handleQueueRun}
              >
                {state.queue?.status === 'CancelRequested'
                  ? 'Cancel requested'
                  : hasPendingOrActiveRun
                    ? 'Cancel run'
                    : 'Queue run'}
              </button>
            </div>
          </div>
        </article>

        <article className="vf-panel">
          <h2 className="vf-panel-title">Recurring Schedule</h2>
          <dl className="vf-kv">
            <div><dt>Enabled</dt><dd>{state.schedule?.enabled ? 'Yes' : 'No'}</dd></div>
            <div><dt>Interval</dt><dd>{state.schedule?.intervalMinutes ?? 30} minutes</dd></div>
            <div><dt>Next Run</dt><dd>{formatDate(state.schedule?.nextRunAt ?? null)}</dd></div>
            <div><dt>Last Scheduled</dt><dd>{formatDate(state.schedule?.lastScheduledRunAt ?? null)}</dd></div>
            <div><dt>Last Enqueue Attempt</dt><dd>{formatDate(state.schedule?.lastEnqueueAttemptAt ?? null)}</dd></div>
          </dl>
          {state.schedule?.lastEnqueueError ? (
            <p className="vf-callout vf-callout-warn mt-5">{state.schedule.lastEnqueueError}</p>
          ) : null}
          <div className="vf-form-grid mt-5">
            <label className="vf-checkbox-row">
              <span>Enabled</span>
              <input checked={scheduleEnabled} type="checkbox" onChange={(event) => setScheduleEnabled(event.target.checked)} />
            </label>
            <label>
              <span>Interval minutes</span>
              <input
                className="vf-input"
                max={1440}
                min={5}
                step={1}
                type="number"
                value={intervalMinutes}
                onChange={(event) => setIntervalMinutes(Number(event.target.value))}
              />
            </label>
            <div className="vf-filter-actions">
              <button className="vf-primary-button" type="button" onClick={handleSaveSchedule}>
                Save schedule
              </button>
            </div>
          </div>
        </article>
      </section>

      {deleteConfirmOpen ? (
        <section className="vf-dialog-backdrop">
          <div className="vf-dialog">
            <div className="vf-dialog-panel">
              <p className="vf-eyebrow">Danger Zone</p>
              <h2 className="vf-panel-title">Delete All Users</h2>
              <p className="mt-4 text-[17px] leading-[1.47] tracking-[-0.022em] text-black/62">
                This queues a live delete job against all users currently returned by the worker source.
                Type <strong>DELETE ALL USERS</strong> to continue.
              </p>
              <label className="mt-5 block">
                <span>Confirmation text</span>
                <input
                  autoFocus
                  className="vf-input mt-2"
                  type="text"
                  value={deleteConfirmationText}
                  onChange={(event) => setDeleteConfirmationText(event.target.value)}
                />
              </label>
              <div className="vf-filter-actions mt-5">
                <button className="vf-secondary-button" type="button" onClick={() => setDeleteConfirmOpen(false)}>
                  Cancel
                </button>
                <button
                  className="vf-danger-button"
                  disabled={deleteConfirmationText.trim() !== 'DELETE ALL USERS'}
                  type="button"
                  onClick={handleDeleteAllUsers}
                >
                  Queue delete-all run
                </button>
              </div>
            </div>
          </div>
        </section>
      ) : null}

      <section className="vf-panel">
        <div className="vf-section-heading">
          <div>
            <p className="vf-eyebrow">History</p>
            <h2 className="vf-panel-title">Recent Runs</h2>
          </div>
          <Link className="vf-primary-link" to="/">
            Back to dashboard
          </Link>
        </div>

        {runs.length === 0 ? (
          <p className="mt-5 text-[17px] leading-[1.47] tracking-[-0.022em] text-black/62">
            {state.loading ? 'Loading runs...' : 'No runs have been recorded yet.'}
          </p>
        ) : (
          <>
            <div className="mt-5 overflow-x-auto">
              <table className="vf-table">
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
                  {runs.slice(0, 25).map((run: RunSummary) => (
                    <tr key={run.runId}>
                      <td>{formatDate(run.startedAt)}</td>
                      <td>{run.runTrigger}</td>
                      <td><span className={`vf-badge ${statusTone(run.status)}`}>{run.status}</span></td>
                      <td>{run.syncScope}</td>
                      <td>{run.dryRun ? 'Yes' : 'No'}</td>
                      <td>{run.totalWorkers}</td>
                      <td>{run.requestedBy ?? 'n/a'}</td>
                      <td><Link className="vf-inline-link" to={`/runs/${run.runId}`}>Open</Link></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="vf-table-pagination">
              <p className="text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
                Showing page 1 of 1 ({runs.length} total runs).
              </p>
            </div>
          </>
        )}
      </section>
    </>
  )
}
