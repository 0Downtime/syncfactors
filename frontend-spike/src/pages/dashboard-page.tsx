import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  Activity,
  AlertTriangle,
  ArrowRight,
  Clock3,
  DatabaseZap,
  RefreshCcw,
  ShieldCheck,
  Siren,
} from 'lucide-react'

import { api } from '@/lib/api'
import { formatDate, runSummaryLine, statusTone } from '@/lib/format'
import type { DashboardSnapshot, DependencyHealthSnapshot, RunSummary } from '@/lib/types'

type State = {
  dashboard: DashboardSnapshot | null
  health: DependencyHealthSnapshot | null
  error: string | null
  loading: boolean
}

const initialState: State = { dashboard: null, health: null, error: null, loading: true }

function formatFreshness(value: string | null) {
  if (!value) return 'Waiting for first signal'

  const diffMs = Date.now() - new Date(value).getTime()
  const diffMinutes = Math.max(0, Math.round(diffMs / 60000))

  if (diffMinutes < 1) return 'Updated just now'
  if (diffMinutes === 1) return 'Updated 1 minute ago'
  if (diffMinutes < 60) return `Updated ${diffMinutes} minutes ago`

  const diffHours = Math.round(diffMinutes / 60)
  if (diffHours === 1) return 'Updated 1 hour ago'
  return `Updated ${diffHours} hours ago`
}

function formatDuration(seconds: number | null) {
  if (seconds == null || seconds < 0) return 'n/a'
  if (seconds < 60) return `${seconds}s`

  const minutes = Math.floor(seconds / 60)
  const remainingSeconds = seconds % 60
  if (minutes < 60) return remainingSeconds ? `${minutes}m ${remainingSeconds}s` : `${minutes}m`

  const hours = Math.floor(minutes / 60)
  const remainingMinutes = minutes % 60
  return remainingMinutes ? `${hours}h ${remainingMinutes}m` : `${hours}h`
}

function runProgress(run: RunSummary | null) {
  if (!run || run.totalWorkers <= 0) return 0
  return Math.max(0, Math.min(100, Math.round((run.processedWorkers / run.totalWorkers) * 100)))
}

function RuntimeStat({
  label,
  value,
  meta,
}: {
  label: string
  value: string
  meta: string
}) {
  return (
    <article className="vf-stat-card">
      <p className="vf-stat-label">{label}</p>
      <p className="vf-stat-value">{value}</p>
      <p className="vf-stat-meta">{meta}</p>
    </article>
  )
}

function HealthCard({
  name,
  status,
  summary,
  details,
}: {
  name: string
  status: string
  summary: string
  details: string | null
}) {
  return (
    <article className="vf-health-card">
      <div className="vf-health-card-header">
        <div>
          <p className="vf-list-title">{name}</p>
          <p className="vf-list-meta">{summary}</p>
        </div>
        <span className={`vf-badge ${statusTone(status)}`}>{status}</span>
      </div>
      <p className="text-[14px] leading-[1.43] tracking-[-0.016em] text-[color:var(--vf-ink-soft)]">
        {details ?? 'No additional probe details were recorded.'}
      </p>
    </article>
  )
}

function RunListItem({
  run,
  label,
}: {
  run: RunSummary
  label: string
}) {
  return (
    <article className="vf-list-row">
      <div>
        <p className="vf-list-title">{run.runId}</p>
        <p className="vf-list-meta">
          {label} · {run.mode} · {run.dryRun ? 'Dry run' : 'Live'} · {formatDate(run.startedAt)}
        </p>
        <p className="vf-list-meta">{runSummaryLine(run)}</p>
      </div>
      <div className="flex flex-col items-end gap-3">
        <span className={`vf-badge ${statusTone(run.status)}`}>{run.status}</span>
        <Link className="vf-inline-link" to={`/runs/${run.runId}`}>
          Open run
        </Link>
      </div>
    </article>
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
  const status = dashboard?.status ?? null
  const runs = dashboard?.runs ?? []
  const probes = health?.probes ?? []
  const progress = runProgress(dashboard?.activeRun ?? null)

  const attentionCount = useMemo(() => {
    if (!dashboard) return 0
    const latestRun = dashboard.runs[0]
    let count = 0
    if (dashboard.requiresAttention) count += 1
    if (latestRun?.guardrailFailures) count += latestRun.guardrailFailures
    if (latestRun?.conflicts) count += latestRun.conflicts
    return count
  }, [dashboard])

  const latestRun = runs[0] ?? null
  const healthyProbeCount = probes.filter((probe) => statusTone(probe.status) === 'good').length
  const degradedProbeCount = probes.filter((probe) => statusTone(probe.status) !== 'good').length

  return (
    <>
      <section className="vf-hero vf-scroll-target" id="dashboard">
        <div className="max-w-[54rem]">
          <p className="vf-eyebrow vf-eyebrow-dark">Current Runtime</p>
          <h1 className="vf-hero-title">Operator Dashboard</h1>
          <p className="vf-hero-lede">
            Monitor the live sync pipeline, spot operator risk early, and move from health to run
            detail without falling into raw tables first.
          </p>
          <div className="mt-6 flex flex-wrap gap-3">
            <Link className="vf-primary-link" to="/sync">
              Manage sync
            </Link>
            <Link className="vf-secondary-button" to="/preview">
              Inspect a worker
            </Link>
          </div>
        </div>

        <div className="w-full max-w-[360px] rounded-[28px] border border-white/12 bg-white/8 p-5 shadow-[0_18px_40px_rgba(0,0,0,0.18)] backdrop-blur-[18px]">
          <div className="flex items-start justify-between gap-4">
            <div>
              <p className="vf-eyebrow vf-eyebrow-dark">Runtime pulse</p>
              <p className="mt-2 text-[28px] font-semibold tracking-[-0.03em] text-white">
                {status?.status ?? 'Unknown'}
              </p>
            </div>
            <span className={`vf-badge ${statusTone(status?.status ?? 'unknown')}`}>
              {state.loading ? 'Refreshing' : 'Live'}
            </span>
          </div>

          <div className="vf-runtime-progress">
            <div className="vf-progress-row">
              <span>Active run progress</span>
              <strong className="text-white">{progress}%</strong>
            </div>
            <div className="vf-progress-track">
              <span className="vf-progress-bar" style={{ width: `${progress}%` }} />
            </div>
            <p className="text-[14px] leading-[1.43] tracking-[-0.016em] text-white/70">
              {dashboard?.activeRun
                ? `${dashboard.activeRun.processedWorkers} of ${dashboard.activeRun.totalWorkers} workers processed`
                : 'No active run is currently processing workers.'}
            </p>
          </div>

          <div className="vf-mini-grid">
            <dl className="vf-mini-card">
              <dt>Current stage</dt>
              <dd>{status?.stage ?? 'Unknown'}</dd>
            </dl>
            <dl className="vf-mini-card">
              <dt>Last refresh</dt>
              <dd>{formatFreshness(dashboard?.checkedAt ?? null)}</dd>
            </dl>
          </div>
        </div>
      </section>

      <section className="vf-runtime-strip">
        <RuntimeStat
          label="Operator Risk"
          value={attentionCount ? `${attentionCount}` : 'Clear'}
          meta={
            dashboard?.requiresAttention
              ? dashboard.attentionMessage ?? 'Operator review is required.'
              : 'No elevated risk signal is currently active.'
          }
        />
        <RuntimeStat
          label="Pipeline State"
          value={status?.status ?? 'Unknown'}
          meta={`${status?.stage ?? 'Unknown stage'} · ${status?.lastAction ?? 'No recent action'}`}
        />
        <RuntimeStat
          label="Dependency Health"
          value={probes.length ? `${healthyProbeCount}/${probes.length}` : 'Pending'}
          meta={
            probes.length
              ? degradedProbeCount
                ? `${degradedProbeCount} dependency signals need attention`
                : 'All probes currently reporting healthy'
              : 'Health probes have not reported yet'
          }
        />
        <RuntimeStat
          label="Latest Run"
          value={latestRun ? formatDuration(latestRun.durationSeconds) : 'n/a'}
          meta={
            latestRun
              ? `${latestRun.mode} · ${latestRun.dryRun ? 'Dry run' : 'Live run'}`
              : 'No completed run is available yet'
          }
        />
      </section>

      {state.error ? (
        <section className="vf-panel">
          <p className="vf-callout vf-callout-danger">
            The dashboard could not refresh live data. {state.error}
          </p>
        </section>
      ) : null}

      {dashboard?.requiresAttention ? (
        <section className="vf-panel">
          <div className="flex items-start gap-3">
            <Siren className="mt-1 size-5 text-[color:var(--vf-bad)]" />
            <div>
              <p className="vf-panel-kicker">Requires attention</p>
              <p className="vf-callout vf-callout-danger">
                {dashboard.attentionMessage ?? 'Operator attention is required.'}
              </p>
            </div>
          </div>
        </section>
      ) : null}

      <section className="vf-dashboard-grid">
        <div className="vf-panel-stack">
          <article className="vf-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">Live operations</p>
                <h2 className="vf-panel-title">Runtime activity</h2>
              </div>
              <span className={`vf-badge ${statusTone(status?.status ?? 'unknown')}`}>
                {status?.status ?? 'Unknown'}
              </span>
            </div>

            <dl className="vf-kv">
              <div><dt>Run Id</dt><dd>{status?.runId ?? 'None'}</dd></div>
              <div><dt>Worker</dt><dd>{status?.currentWorkerId ?? 'None'}</dd></div>
              <div><dt>Progress</dt><dd>{status ? `${status.processedWorkers} / ${status.totalWorkers}` : '0 / 0'}</dd></div>
              <div><dt>Last action</dt><dd>{status?.lastAction ?? 'None'}</dd></div>
              <div><dt>Started</dt><dd>{formatDate(status?.startedAt ?? null)}</dd></div>
              <div><dt>Last updated</dt><dd>{formatDate(status?.lastUpdatedAt ?? null)}</dd></div>
            </dl>

            {status?.errorMessage ? (
              <p className="vf-callout vf-callout-danger mt-5">{status.errorMessage}</p>
            ) : null}

            <div className="vf-mini-grid">
              <dl className="vf-mini-card">
                <dt>Run mode</dt>
                <dd>{dashboard?.activeRun?.mode ?? status?.mode ?? 'Idle'}</dd>
              </dl>
              <dl className="vf-mini-card">
                <dt>Dry run</dt>
                <dd>{dashboard?.activeRun ? (dashboard.activeRun.dryRun ? 'Yes' : 'No') : 'n/a'}</dd>
              </dl>
            </div>
          </article>

          <article className="vf-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">Health surface</p>
                <h2 className="vf-panel-title">Dependency health</h2>
              </div>
              <div className="flex flex-col items-end gap-2">
                <span className={`vf-badge ${statusTone(health?.status ?? 'unknown')}`}>
                  {health?.status ?? 'Loading'}
                </span>
                <span className="vf-caption">{formatFreshness(health?.checkedAt ?? null)}</span>
              </div>
            </div>

            <div className="mt-5 grid grid-cols-4 gap-2" aria-hidden="true">
              {['SuccessFactors', 'Active Directory', 'Worker Service', 'SQLite'].map((name) => {
                const probe = probes.find((item) => item.dependency === name)
                return (
                  <span
                    key={name}
                    className={`vf-connection-segment ${statusTone(probe?.status ?? 'unknown')}`}
                  />
                )
              })}
            </div>

            <div className="vf-health-grid">
              {(probes.length
                ? probes
                : [
                    {
                      dependency: 'SuccessFactors',
                      status: 'Unknown',
                      summary: 'Probe pending.',
                      details: 'The dashboard will run this check after the page settles.',
                    },
                    {
                      dependency: 'Active Directory',
                      status: 'Unknown',
                      summary: 'Probe pending.',
                      details: 'The dashboard will run this check after the page settles.',
                    },
                    {
                      dependency: 'Worker Service',
                      status: 'Unknown',
                      summary: 'Probe pending.',
                      details: 'The dashboard will run this check after the page settles.',
                    },
                    {
                      dependency: 'SQLite',
                      status: 'Unknown',
                      summary: 'Probe pending.',
                      details: 'The dashboard will run this check after the page settles.',
                    },
                  ]
              ).map((probe) => (
                <HealthCard
                  key={probe.dependency}
                  details={probe.details}
                  name={probe.dependency}
                  status={probe.status}
                  summary={probe.summary}
                />
              ))}
            </div>
          </article>
        </div>

        <div className="vf-panel-stack">
          <article className="vf-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">Run posture</p>
                <h2 className="vf-panel-title">Operational focus</h2>
              </div>
              <Activity className="size-5 text-[color:var(--vf-neutral)]" />
            </div>

            <div className="vf-list">
              {dashboard?.activeRun ? (
                <article className="vf-list-row">
                  <div>
                    <p className="vf-list-title">Active run in progress</p>
                    <p className="vf-list-meta">
                      {dashboard.activeRun.mode} · {dashboard.activeRun.processedWorkers} /{' '}
                      {dashboard.activeRun.totalWorkers} workers
                    </p>
                  </div>
                  <Link className="vf-inline-link" to={`/runs/${dashboard.activeRun.runId}`}>
                    Open <ArrowRight className="ml-1 inline size-4" />
                  </Link>
                </article>
              ) : (
                <div className="vf-empty-state">No active run. The worker is currently idle.</div>
              )}

              {dashboard?.lastCompletedRun ? (
                <article className="vf-list-row">
                  <div>
                    <p className="vf-list-title">Last completed run</p>
                    <p className="vf-list-meta">
                      {dashboard.lastCompletedRun.status} · {dashboard.lastCompletedRun.mode} ·{' '}
                      {formatDate(dashboard.lastCompletedRun.completedAt)}
                    </p>
                  </div>
                  <Link className="vf-inline-link" to={`/runs/${dashboard.lastCompletedRun.runId}`}>
                    Review <ArrowRight className="ml-1 inline size-4" />
                  </Link>
                </article>
              ) : null}

              <article className="vf-list-row">
                <div>
                  <p className="vf-list-title">Review the next risky worker</p>
                  <p className="vf-list-meta">
                    Jump directly into preview and inspect attribute-level change risk before a real
                    sync.
                  </p>
                </div>
                <Link className="vf-inline-link" to="/preview">
                  Open preview <ArrowRight className="ml-1 inline size-4" />
                </Link>
              </article>
            </div>
          </article>

          <article className="vf-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">Recent activity</p>
                <h2 className="vf-panel-title">Runs</h2>
              </div>
              <RefreshCcw
                className={`size-5 text-[color:var(--vf-ink-faint)] ${state.loading ? 'animate-spin' : ''}`}
              />
            </div>

            {runs.length === 0 ? (
              <div className="vf-empty-state">
                {state.loading
                  ? 'Loading runs...'
                  : 'No run data is configured yet. Set SyncFactors__SqlitePath to point the UI at an operational SQLite store.'}
              </div>
            ) : (
              <div className="vf-list">
                {runs.slice(0, 4).map((run, index) => (
                  <RunListItem key={run.runId} label={index === 0 ? 'Most recent' : 'Recent run'} run={run} />
                ))}
              </div>
            )}
          </article>

          <article className="vf-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">Operator cues</p>
                <h2 className="vf-panel-title">What needs attention</h2>
              </div>
              <ShieldCheck className="size-5 text-[color:var(--vf-good)]" />
            </div>

            <div className="vf-list">
              <article className="vf-list-row">
                <div>
                  <p className="vf-list-title">Guardrail pressure</p>
                  <p className="vf-list-meta">
                    {latestRun?.guardrailFailures
                      ? `${latestRun.guardrailFailures} guardrail failures landed in the latest run`
                      : 'No guardrail failures were recorded in the latest run'}
                  </p>
                </div>
                <AlertTriangle className="size-5 text-[color:var(--vf-warn)]" />
              </article>

              <article className="vf-list-row">
                <div>
                  <p className="vf-list-title">Conflict count</p>
                  <p className="vf-list-meta">
                    {latestRun?.conflicts
                      ? `${latestRun.conflicts} worker conflicts need review`
                      : 'No worker conflicts are visible in the latest run'}
                  </p>
                </div>
                <DatabaseZap className="size-5 text-[color:var(--vf-neutral)]" />
              </article>

              <article className="vf-list-row">
                <div>
                  <p className="vf-list-title">Freshness window</p>
                  <p className="vf-list-meta">{formatFreshness(dashboard?.checkedAt ?? null)}</p>
                </div>
                <Clock3 className="size-5 text-[color:var(--vf-ink-faint)]" />
              </article>
            </div>
          </article>
        </div>
      </section>
    </>
  )
}
