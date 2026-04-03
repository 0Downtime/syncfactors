import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'

import { api } from '@/lib/api'
import { formatDate, runSummaryLine, statusTone } from '@/lib/format'
import type { DashboardSnapshot, DependencyHealthSnapshot } from '@/lib/types'

type State = {
  dashboard: DashboardSnapshot | null
  health: DependencyHealthSnapshot | null
  error: string | null
  loading: boolean
}

const initialState: State = { dashboard: null, health: null, error: null, loading: true }

function RunHealthCard({
  title,
  run,
  emptyMessage,
  actionLabel,
}: {
  title: string
  run: DashboardSnapshot['activeRun']
  emptyMessage: string
  actionLabel: string
}) {
  return (
    <section className="vf-run-health-card">
      <p className="vf-eyebrow">{title}</p>
      {run ? (
        <div className="space-y-3">
          <p className="text-[17px] font-semibold leading-[1.24] tracking-[-0.022em] text-[#1d1d1f]">
            {run.runId}
          </p>
          <p className="text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
            {title === 'Active Run'
              ? `${run.mode} · ${run.processedWorkers} / ${run.totalWorkers} workers`
              : `${run.status} · ${run.mode} · ${run.totalWorkers} workers`}
          </p>
          <p>
            <Link className="vf-inline-link" to={`/runs/${run.runId}`}>
              {actionLabel}
            </Link>
          </p>
        </div>
      ) : (
        <p className="text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">{emptyMessage}</p>
      )}
    </section>
  )
}

export function DashboardPage() {
  const [state, setState] = useState(initialState)

  useEffect(() => {
    let mounted = true

    async function load() {
      try {
        if (mounted) setState((current) => ({ ...current, loading: true, error: null }))
        const [dashboard, health] = await Promise.all([api.dashboard(), api.health()])
        if (mounted) setState({ dashboard, health, error: null, loading: false })
      } catch (error) {
        if (mounted) {
          setState((current) => ({
            ...current,
            error: error instanceof Error ? error.message : 'Unable to load dashboard.',
            loading: false,
          }))
        }
      }
    }

    void load()
    const timer = window.setInterval(() => void load(), 15000)
    return () => {
      mounted = false
      window.clearInterval(timer)
    }
  }, [])

  const dashboard = state.dashboard
  const health = state.health
  const status = dashboard?.status
  const probes = health?.probes ?? []
  const runs = dashboard?.runs ?? []

  return (
    <>
      <section className="vf-hero vf-scroll-target" id="dashboard">
        <div>
          <p className="vf-eyebrow vf-eyebrow-dark">Current Runtime</p>
          <h1 className="vf-hero-title">Operator Dashboard</h1>
          <p className="vf-hero-lede">
            This page reads runtime status and recent runs from the configured operational store
            for the .NET rewrite.
          </p>
        </div>
        <div className="flex flex-wrap gap-3">
          <Link className="vf-primary-link" to="/sync">
            Manage sync
          </Link>
        </div>
      </section>

      {dashboard?.requiresAttention ? (
        <section className="vf-panel">
          <p className="vf-callout vf-callout-danger">
            {dashboard.attentionMessage ?? 'Operator attention is required.'}
          </p>
        </section>
      ) : null}

      <section className="vf-panel-grid">
        <article className="vf-panel">
          <h2 className="vf-panel-title">Status</h2>
          <dl className="vf-kv">
            <div><dt>Status</dt><dd>{status?.status ?? 'Unknown'}</dd></div>
            <div><dt>Stage</dt><dd>{status?.stage ?? 'Unknown'}</dd></div>
            <div><dt>Run Id</dt><dd>{status?.runId ?? 'None'}</dd></div>
            <div><dt>Worker</dt><dd>{status?.currentWorkerId ?? 'None'}</dd></div>
            <div><dt>Progress</dt><dd>{status ? `${status.processedWorkers} / ${status.totalWorkers}` : '0 / 0'}</dd></div>
            <div><dt>Last Action</dt><dd>{status?.lastAction ?? 'None'}</dd></div>
            <div><dt>Last Updated</dt><dd>{formatDate(status?.lastUpdatedAt ?? null)}</dd></div>
          </dl>

          {status?.errorMessage ? (
            <p className="vf-callout vf-callout-danger mt-5">{status.errorMessage}</p>
          ) : null}

          <section className="mt-8">
            <div className="flex flex-wrap items-start justify-between gap-4">
              <div>
                <p className="vf-eyebrow">Connection</p>
                <h3 className="vf-section-title">Dependency Health</h3>
              </div>
              <div className="text-right">
                <span className={`vf-badge ${statusTone(health?.status ?? 'unknown')}`}>
                  {health?.status ?? 'Loading'}
                </span>
                <p className="mt-2 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
                  {health ? formatDate(health.checkedAt) : 'Probe has not run yet.'}
                </p>
              </div>
            </div>

            <div className="mt-5 grid grid-cols-4 gap-2" aria-hidden="true">
              {['SuccessFactors', 'Active Directory', 'Worker Service', 'SQLite'].map((name) => {
                const probe = probes.find((item) => item.dependency === name)
                return <span key={name} className={`vf-connection-segment ${statusTone(probe?.status ?? 'unknown')}`} />
              })}
            </div>

            <div className="mt-5 grid gap-3">
              {(probes.length
                ? probes
                : [
                    { dependency: 'SuccessFactors', status: 'Unknown', summary: 'Probe pending.', details: 'The dashboard will run this check after the page settles.' },
                    { dependency: 'Active Directory', status: 'Unknown', summary: 'Probe pending.', details: 'The dashboard will run this check after the page settles.' },
                    { dependency: 'Worker Service', status: 'Unknown', summary: 'Probe pending.', details: 'The dashboard will run this check after the page settles.' },
                    { dependency: 'SQLite', status: 'Unknown', summary: 'Probe pending.', details: 'The dashboard will run this check after the page settles.' },
                  ]
              ).map((probe) => (
                <article key={probe.dependency} className="vf-connection-card">
                  <header className="flex items-start justify-between gap-4">
                    <h4 className="vf-connection-title">{probe.dependency}</h4>
                    <span className={`vf-badge ${statusTone(probe.status)}`}>{probe.status}</span>
                  </header>
                  <p className="mt-3 text-[17px] leading-[1.47] tracking-[-0.022em] text-[#1d1d1f]">
                    {probe.summary}
                  </p>
                  <p className="mt-2 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
                    {probe.details}
                  </p>
                </article>
              ))}
            </div>
          </section>
        </article>

        <article className="vf-panel">
          <h2 className="vf-panel-title">Run Health</h2>
          <div className="mt-5 grid gap-4">
            <RunHealthCard
              title="Active Run"
              run={dashboard?.activeRun ?? null}
              emptyMessage="No run is active."
              actionLabel="Open active run"
            />
            <RunHealthCard
              title="Last Completed"
              run={dashboard?.lastCompletedRun ?? null}
              emptyMessage="No completed runs yet."
              actionLabel="Review last run"
            />
          </div>
        </article>

        <article className="vf-panel">
          <h2 className="vf-panel-title">Recent Runs</h2>
          <p className="mt-4 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
            Live data is refreshed automatically while this page is open.
          </p>

          {state.error ? (
            <div className="mt-5 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
              The dashboard could not reach the API.
            </div>
          ) : runs.length === 0 ? (
            <div className="mt-5 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
              {state.loading
                ? 'Loading runs...'
                : 'No run data is configured yet. Set SyncFactors__SqlitePath to point the rewrite UI at an operational SQLite store.'}
            </div>
          ) : (
            <div className="mt-5 overflow-x-auto">
              <table className="vf-table">
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
                  {runs.map((run) => (
                    <tr key={run.runId}>
                      <td>{formatDate(run.startedAt)}</td>
                      <td>{run.runTrigger}</td>
                      <td>{run.mode}</td>
                      <td><span className={`vf-badge ${statusTone(run.status)}`}>{run.status}</span></td>
                      <td>{run.dryRun ? 'Yes' : 'No'}</td>
                      <td>{run.totalWorkers}</td>
                      <td>{runSummaryLine(run)}</td>
                      <td><Link className="vf-inline-link" to={`/runs/${run.runId}`}>Open</Link></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </article>
      </section>
    </>
  )
}
