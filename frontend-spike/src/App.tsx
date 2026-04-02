import { useEffect, useState } from 'react'
import { RefreshCcw } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'

type RuntimeStatus = {
  status: string
  stage: string
  runId: string | null
  mode: string | null
  dryRun: boolean
  processedWorkers: number
  totalWorkers: number
  currentWorkerId: string | null
  lastAction: string | null
  startedAt: string | null
  lastUpdatedAt: string | null
  completedAt: string | null
  errorMessage: string | null
}

type RunSummary = {
  runId: string
  mode: string
  dryRun: boolean
  status: string
  startedAt: string
  completedAt: string | null
  processedWorkers: number
  totalWorkers: number
  creates: number
  updates: number
  enables: number
  disables: number
  graveyardMoves: number
  deletions: number
  quarantined: number
  conflicts: number
  guardrailFailures: number
  manualReview: number
  unchanged: number
  runTrigger: string
}

type DashboardSnapshot = {
  status: RuntimeStatus
  runs: RunSummary[]
  activeRun: RunSummary | null
  lastCompletedRun: RunSummary | null
  requiresAttention: boolean
  attentionMessage: string | null
  checkedAt: string
}

type DependencyProbeResult = {
  dependency: string
  status: string
  summary: string
  details: string | null
  checkedAt: string
  durationMilliseconds: number
  observedAt: string | null
  isStale: boolean
}

type DependencyHealthSnapshot = {
  status: string
  checkedAt: string
  probes: DependencyProbeResult[]
}

type SnapshotState = {
  dashboard: DashboardSnapshot | null
  health: DependencyHealthSnapshot | null
  error: string | null
  loading: boolean
}

const initialState: SnapshotState = {
  dashboard: null,
  health: null,
  error: null,
  loading: true,
}

function formatDate(value: string | null) {
  if (!value) {
    return 'Unknown'
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(value))
}

function healthTone(status: string) {
  switch (status.toLowerCase()) {
    case 'healthy':
    case 'succeeded':
    case 'completed':
    case 'idle':
      return 'good'
    case 'degraded':
    case 'queued':
    case 'running':
      return 'warn'
    case 'failed':
    case 'unhealthy':
    case 'error':
      return 'bad'
    default:
      return 'neutral'
  }
}

function runSummaryLine(run: RunSummary) {
  const parts: string[] = []

  if (run.creates) {
    parts.push(`${run.creates} creates`)
  }

  if (run.updates) {
    parts.push(`${run.updates} updates`)
  }

  if (run.conflicts) {
    parts.push(`${run.conflicts} conflicts`)
  }

  if (run.manualReview) {
    parts.push(`${run.manualReview} manual review`)
  }

  if (run.guardrailFailures) {
    parts.push(`${run.guardrailFailures} guardrails`)
  }

  return parts.length ? parts.join(' • ') : 'No materialized bucket counts yet'
}

function RunHealthCard({
  title,
  run,
  emptyMessage,
  actionLabel,
}: {
  title: string
  run: RunSummary | null
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
            <a href="#" className="vf-inline-link">
              {actionLabel}
            </a>
          </p>
        </div>
      ) : (
        <p className="text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">{emptyMessage}</p>
      )}
    </section>
  )
}

function App() {
  const [state, setState] = useState(initialState)

  useEffect(() => {
    let isMounted = true

    async function load() {
      try {
        if (isMounted) {
          setState((current) => ({ ...current, loading: true, error: null }))
        }

        const [dashboardResponse, healthResponse] = await Promise.all([
          fetch('/api/dashboard'),
          fetch('/api/health'),
        ])

        if (!dashboardResponse.ok || !healthResponse.ok) {
          throw new Error('The API did not return a successful response.')
        }

        const [dashboard, health] = await Promise.all([
          dashboardResponse.json() as Promise<DashboardSnapshot>,
          healthResponse.json() as Promise<DependencyHealthSnapshot>,
        ])

        if (isMounted) {
          setState({
            dashboard,
            health,
            error: null,
            loading: false,
          })
        }
      } catch (error) {
        if (isMounted) {
          setState((current) => ({
            ...current,
            error:
              error instanceof Error
                ? error.message
                : 'The spike could not reach the SyncFactors API.',
            loading: false,
          }))
        }
      }
    }

    void load()

    const intervalId = window.setInterval(() => {
      void load()
    }, 15000)

    return () => {
      isMounted = false
      window.clearInterval(intervalId)
    }
  }, [])

  const dashboard = state.dashboard
  const health = state.health
  const status = dashboard?.status
  const probes = health?.probes ?? []
  const runs = dashboard?.runs ?? []

  return (
    <main className="min-h-screen bg-[#f5f5f7] text-[#1d1d1f]">
      <div className="sticky top-0 z-50 px-4 pt-3 md:px-6">
        <div className="mx-auto flex h-12 w-full max-w-[1200px] items-center justify-between rounded-full border border-white/10 bg-black/80 px-4 text-white backdrop-blur-[20px]">
          <div className="flex items-center gap-3">
            <span className="text-[13px] font-medium tracking-[-0.01em]">SyncFactors</span>
            <Badge className="vf-nav-badge">Vite Dashboard</Badge>
          </div>
          <div className="hidden items-center gap-1 md:flex">
            <a className="vf-nav-link" href="#dashboard">Dashboard</a>
            <a className="vf-nav-link" href="#sync">Sync</a>
            <a className="vf-nav-link" href="#preview">Worker Preview</a>
          </div>
          <Button className="vf-nav-button" onClick={() => window.location.reload()}>
            <RefreshCcw className="size-4" />
            Refresh
          </Button>
        </div>
      </div>

      <div className="mx-auto flex w-full max-w-[1200px] flex-col gap-5 px-4 py-5 md:px-6 md:pb-10">
        <section id="dashboard" className="vf-hero vf-scroll-target">
          <div>
            <p className="vf-eyebrow vf-eyebrow-dark">Current Runtime</p>
            <h1 className="vf-hero-title">Operator Dashboard</h1>
            <p className="vf-hero-lede">
              This page reads runtime status and recent runs from the configured operational store
              for the .NET rewrite.
            </p>
          </div>
          <div className="flex flex-wrap gap-3">
            <Button className="vf-primary-button">Manage sync</Button>
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
                  <span className={`vf-badge ${healthTone(health?.status ?? 'unknown')}`}>
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
                  return <span key={name} className={`vf-connection-segment ${healthTone(probe?.status ?? 'unknown')}`} />
                })}
              </div>

              <div className="mt-5 grid gap-3">
                {(probes.length
                  ? probes
                  : [
                      {
                        dependency: 'SuccessFactors',
                        status: 'Unknown',
                        summary: 'Probe pending.',
                        details: 'The dashboard will run this check after the page settles.',
                        checkedAt: '',
                        durationMilliseconds: 0,
                        observedAt: null,
                        isStale: false,
                      },
                      {
                        dependency: 'Active Directory',
                        status: 'Unknown',
                        summary: 'Probe pending.',
                        details: 'The dashboard will run this check after the page settles.',
                        checkedAt: '',
                        durationMilliseconds: 0,
                        observedAt: null,
                        isStale: false,
                      },
                      {
                        dependency: 'Worker Service',
                        status: 'Unknown',
                        summary: 'Probe pending.',
                        details: 'The dashboard will run this check after the page settles.',
                        checkedAt: '',
                        durationMilliseconds: 0,
                        observedAt: null,
                        isStale: false,
                      },
                      {
                        dependency: 'SQLite',
                        status: 'Unknown',
                        summary: 'Probe pending.',
                        details: 'The dashboard will run this check after the page settles.',
                        checkedAt: '',
                        durationMilliseconds: 0,
                        observedAt: null,
                        isStale: false,
                      },
                    ]
                ).map((probe) => (
                  <article key={probe.dependency} className="vf-connection-card">
                    <header className="flex items-start justify-between gap-4">
                      <h4 className="vf-connection-title">{probe.dependency}</h4>
                      <span className={`vf-badge ${healthTone(probe.status)}`}>{probe.status}</span>
                    </header>
                    <p className="mt-3 text-[17px] leading-[1.47] tracking-[-0.022em] text-[#1d1d1f]">
                      {probe.summary}
                    </p>
                    <p className="mt-2 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
                      {probe.details ?? 'The dashboard will run this check after the page settles.'}
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
                No run data is configured yet. Set <code>SyncFactors__SqlitePath</code> to point
                the rewrite UI at an operational SQLite store.
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
                        <td>
                          <span className={`vf-badge ${healthTone(run.status)}`}>{run.status}</span>
                        </td>
                        <td>{run.dryRun ? 'Yes' : 'No'}</td>
                        <td>{run.totalWorkers}</td>
                        <td>{runSummaryLine(run)}</td>
                        <td><a href="#" className="vf-inline-link">Open</a></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </article>
        </section>
      </div>
    </main>
  )
}

export default App
