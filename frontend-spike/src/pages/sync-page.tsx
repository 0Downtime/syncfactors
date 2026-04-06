import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

import { api } from '@/lib/api'
import { formatDate, runSummaryLine, statusTone } from '@/lib/format'
import type { DashboardSnapshot, RunSummary, SyncScheduleStatus } from '@/lib/types'

const RUNS_PAGE_SIZE = 25
const DELETE_ALL_CONFIRMATION = 'DELETE ALL USERS'

export function SyncPage(props: {
  onFlash: (flash: { tone: 'good' | 'danger' | 'warn'; message: string } | null) => void
}) {
  const [searchParams, setSearchParams] = useSearchParams()
  const [dashboard, setDashboard] = useState<DashboardSnapshot | null>(null)
  const [schedule, setSchedule] = useState<SyncScheduleStatus | null>(null)
  const [runs, setRuns] = useState<RunSummary[]>([])
  const [totalRuns, setTotalRuns] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [scheduleEnabled, setScheduleEnabled] = useState(false)
  const [intervalMinutes, setIntervalMinutes] = useState(30)
  const [deleteDialogOpen, setDeleteDialogOpen] = useState(false)
  const [deleteConfirmation, setDeleteConfirmation] = useState('')

  const page = Math.max(1, Number(searchParams.get('page') ?? '1') || 1)

  useEffect(() => {
    let cancelled = false

    void (async () => {
      try {
        const [dashboardSnapshot, scheduleResponse, runResponse] = await Promise.all([
          api.dashboard(),
          api.schedule(),
          api.runs(page, RUNS_PAGE_SIZE),
        ])
        if (!cancelled) {
          setDashboard(dashboardSnapshot)
          setSchedule(scheduleResponse.schedule)
          setScheduleEnabled(scheduleResponse.schedule.enabled)
          setIntervalMinutes(scheduleResponse.schedule.intervalMinutes)
          setRuns(runResponse.runs)
          setTotalRuns(runResponse.total)
          setError(null)
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load sync page.')
        }
      }
    })()

    return () => {
      cancelled = true
    }
  }, [page])

  const totalPages = Math.max(1, Math.ceil(totalRuns / RUNS_PAGE_SIZE))
  const hasPendingOrActiveRun = dashboard?.status.status === 'InProgress'

  async function refresh() {
    const [dashboardSnapshot, scheduleResponse, runResponse] = await Promise.all([
      api.dashboard(),
      api.schedule(),
      api.runs(page, RUNS_PAGE_SIZE),
    ])
    setDashboard(dashboardSnapshot)
    setSchedule(scheduleResponse.schedule)
    setScheduleEnabled(scheduleResponse.schedule.enabled)
    setIntervalMinutes(scheduleResponse.schedule.intervalMinutes)
    setRuns(runResponse.runs)
    setTotalRuns(runResponse.total)
  }

  function setPage(nextPage: number) {
    const next = new URLSearchParams(searchParams)
    if (nextPage <= 1) {
      next.delete('page')
    } else {
      next.set('page', String(nextPage))
    }
    setSearchParams(next)
  }

  return (
    <>
      <section className="vf-hero vf-hero-compact">
        <div className="max-w-[54rem]">
          <p className="vf-eyebrow vf-eyebrow-dark">Provisioning Control</p>
          <h1 className="vf-hero-title">Sync</h1>
          <p className="vf-hero-lede">Queue ad hoc AD provisioning runs and manage the recurring full-sync schedule.</p>
        </div>
        <div className="flex flex-wrap gap-3">
          <button className="vf-danger-button" type="button" disabled={hasPendingOrActiveRun} onClick={() => setDeleteDialogOpen(true)}>
            Testing Reset
          </button>
        </div>
      </section>

      {error ? (
        <section className="vf-panel">
          <p className="vf-callout vf-callout-danger">{error}</p>
        </section>
      ) : null}

      <section className="vf-preview-grid">
        <article className="vf-panel">
          <div className="vf-section-heading">
            <div>
              <p className="vf-panel-kicker">Current runtime</p>
              <h2 className="vf-panel-title">Run controls</h2>
            </div>
            <span className={`vf-badge ${statusTone(dashboard?.status.status)}`}>{dashboard?.status.status ?? 'Loading'}</span>
          </div>

          <dl className="vf-kv mt-5">
            <div><dt>Status</dt><dd>{dashboard?.status.status ?? 'Loading'}</dd></div>
            <div><dt>Stage</dt><dd>{dashboard?.status.stage ?? 'Loading'}</dd></div>
            <div><dt>Run Id</dt><dd>{dashboard?.status.runId ?? 'None'}</dd></div>
            <div><dt>Worker</dt><dd>{dashboard?.status.currentWorkerId ?? 'None'}</dd></div>
            <div><dt>Progress</dt><dd>{dashboard ? `${dashboard.status.processedWorkers} / ${dashboard.status.totalWorkers}` : '0 / 0'}</dd></div>
          </dl>

          <div className="vf-filter-actions mt-5">
            <button
              className="vf-primary-button"
              type="button"
              disabled={hasPendingOrActiveRun}
              onClick={() => {
                void api
                  .createRun({ dryRun: true, mode: 'BulkSync', runTrigger: 'AdHoc' })
                  .then(async () => {
                    props.onFlash({ tone: 'good', message: 'Dry-run sync queued.' })
                    await refresh()
                  })
                  .catch((runError) => {
                    setError(runError instanceof Error ? runError.message : 'Failed to queue run.')
                  })
              }}
            >
              Queue dry run
            </button>
            <button
              className="vf-secondary-button"
              type="button"
              disabled={hasPendingOrActiveRun}
              onClick={() => {
                void api
                  .createRun({ dryRun: false, mode: 'BulkSync', runTrigger: 'AdHoc' })
                  .then(async () => {
                    props.onFlash({ tone: 'good', message: 'Live provisioning run queued.' })
                    await refresh()
                  })
                  .catch((runError) => {
                    setError(runError instanceof Error ? runError.message : 'Failed to queue run.')
                  })
              }}
            >
              Queue live run
            </button>
            <button
              className="vf-secondary-button"
              type="button"
              onClick={() => {
                void api
                  .cancelRun()
                  .then(async () => {
                    props.onFlash({ tone: 'good', message: 'Run cancellation requested.' })
                    await refresh()
                  })
                  .catch((cancelError) => {
                    setError(cancelError instanceof Error ? cancelError.message : 'Failed to cancel run.')
                  })
              }}
            >
              Cancel run
            </button>
          </div>
        </article>

        <article className="vf-panel">
          <div className="vf-section-heading">
            <div>
              <p className="vf-panel-kicker">Recurring schedule</p>
              <h2 className="vf-panel-title">Scheduler</h2>
            </div>
          </div>

          <dl className="vf-kv mt-5">
            <div><dt>Enabled</dt><dd>{schedule?.enabled ? 'Yes' : 'No'}</dd></div>
            <div><dt>Interval</dt><dd>{schedule?.intervalMinutes ?? 30} minutes</dd></div>
            <div><dt>Next Run</dt><dd>{formatDate(schedule?.nextRunAt)}</dd></div>
            <div><dt>Last Scheduled</dt><dd>{formatDate(schedule?.lastScheduledRunAt)}</dd></div>
            <div><dt>Last Enqueue Attempt</dt><dd>{formatDate(schedule?.lastEnqueueAttemptAt)}</dd></div>
          </dl>
          {schedule?.lastEnqueueError ? <p className="vf-callout vf-callout-warn mt-5">{schedule.lastEnqueueError}</p> : null}

          <form
            className="vf-form-grid mt-5"
            onSubmit={(event) => {
              event.preventDefault()
              void api
                .updateSchedule({ enabled: scheduleEnabled, intervalMinutes })
                .then((response) => {
                  setSchedule(response.schedule)
                  props.onFlash({
                    tone: 'good',
                    message: response.schedule.enabled
                      ? `Recurring sync enabled every ${response.schedule.intervalMinutes} minutes.`
                      : 'Recurring sync disabled.',
                  })
                })
                .catch((saveError) => {
                  setError(saveError instanceof Error ? saveError.message : 'Failed to save schedule.')
                })
            }}
          >
            <label className="vf-checkbox-row"><span>Enabled</span><input type="checkbox" checked={scheduleEnabled} onChange={(event) => setScheduleEnabled(event.target.checked)} /></label>
            <label><span>Interval minutes</span><input className="vf-input" type="number" min={5} max={1440} step={1} value={intervalMinutes} onChange={(event) => setIntervalMinutes(Number(event.target.value))} /></label>
            <div className="vf-filter-actions"><button className="vf-primary-button" type="submit">Save schedule</button></div>
          </form>
        </article>
      </section>

      {deleteDialogOpen ? (
        <section className="vf-panel">
          <div className="vf-section-heading">
            <div>
              <p className="vf-panel-kicker">Danger zone</p>
              <h2 className="vf-panel-title">Delete all users</h2>
            </div>
          </div>
          <p className="vf-muted-text mt-3">
            This queues a live delete job against all users currently returned by the worker source. Type{' '}
            <strong>{DELETE_ALL_CONFIRMATION}</strong> to continue.
          </p>
          <label className="mt-5 block">
            <span>Confirmation text</span>
            <input className="vf-input mt-2" value={deleteConfirmation} onChange={(event) => setDeleteConfirmation(event.target.value)} autoComplete="off" />
          </label>
          <div className="vf-filter-actions mt-5">
            <button className="vf-secondary-button" type="button" onClick={() => setDeleteDialogOpen(false)}>Cancel</button>
            <button
              className="vf-danger-button"
              type="button"
              disabled={deleteConfirmation.trim() !== DELETE_ALL_CONFIRMATION}
              onClick={() => {
                void api
                  .queueDeleteAllUsers(deleteConfirmation)
                  .then(async () => {
                    props.onFlash({ tone: 'good', message: 'Delete-all test run queued.' })
                    setDeleteDialogOpen(false)
                    setDeleteConfirmation('')
                    await refresh()
                  })
                  .catch((deleteError) => {
                    setError(deleteError instanceof Error ? deleteError.message : 'Failed to queue delete-all run.')
                  })
              }}
            >
              Queue delete-all run
            </button>
          </div>
        </section>
      ) : null}

      <section className="vf-panel">
        <div className="vf-section-heading">
          <div>
            <p className="vf-panel-kicker">History</p>
            <h2 className="vf-panel-title">Recent runs</h2>
          </div>
        </div>
        {!runs.length ? (
          <p className="vf-muted-text mt-5">No runs have been recorded yet.</p>
        ) : (
          <>
            <div className="mt-5 overflow-x-auto">
              <table className="vf-data-table">
                <thead>
                  <tr>
                    <th>Started</th>
                    <th>Trigger</th>
                    <th>Status</th>
                    <th>Scope</th>
                    <th>Dry Run</th>
                    <th>Workers</th>
                    <th>Requested By</th>
                    <th>Summary</th>
                    <th>Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {runs.map((run) => (
                    <tr key={run.runId}>
                      <td>{formatDate(run.startedAt)}</td>
                      <td>{run.runTrigger}</td>
                      <td><span className={`vf-badge ${statusTone(run.status)}`}>{run.status}</span></td>
                      <td>{run.syncScope}</td>
                      <td>{run.dryRun ? 'Yes' : 'No'}</td>
                      <td>{run.totalWorkers}</td>
                      <td>{run.requestedBy ?? 'n/a'}</td>
                      <td>{runSummaryLine(run)}</td>
                      <td><Link className="vf-inline-link" to={`/runs/${run.runId}`}>Open</Link></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="vf-filter-actions mt-5">
              <p className="vf-muted-text">Showing page {page} of {totalPages} ({totalRuns} total runs).</p>
              {page > 1 ? <button className="vf-secondary-button" type="button" onClick={() => setPage(page - 1)}>Previous</button> : null}
              {page < totalPages ? <button className="vf-secondary-button" type="button" onClick={() => setPage(page + 1)}>Next</button> : null}
            </div>
          </>
        )}
      </section>
    </>
  )
}
