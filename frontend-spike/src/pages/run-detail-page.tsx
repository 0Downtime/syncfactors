import { useEffect, useMemo, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import {
  ArrowRight,
  ChevronDown,
  Clock3,
  Filter,
  Search,
  ShieldAlert,
  UserRoundSearch,
} from 'lucide-react'

import { api } from '@/lib/api'
import { formatDate, runSummaryLine, statusTone } from '@/lib/format'
import type { RunDetail, RunEntry } from '@/lib/types'

type EntriesResponse = {
  run: RunDetail['run']
  entries: RunEntry[]
  total: number
  page: number
  pageSize: number
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

function progressPercent(processedWorkers: number, totalWorkers: number) {
  if (totalWorkers <= 0) return 0
  return Math.max(0, Math.min(100, Math.round((processedWorkers / totalWorkers) * 100)))
}

function savedPreviewRunId(entry: RunEntry) {
  if (entry.artifactType === 'WorkerPreview') {
    return entry.runId
  }

  return typeof entry.item.sourcePreviewRunId === 'string' ? entry.item.sourcePreviewRunId : null
}

export function RunDetailPage() {
  const { runId = '' } = useParams()
  const [searchParams, setSearchParams] = useSearchParams()
  const [runDetail, setRunDetail] = useState<RunDetail | null>(null)
  const [entriesResponse, setEntriesResponse] = useState<EntriesResponse | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const bucket = searchParams.get('bucket') ?? ''
  const workerId = searchParams.get('workerId') ?? ''
  const filter = searchParams.get('filter') ?? ''
  const pageNumber = Number(searchParams.get('page') ?? '1')

  useEffect(() => {
    let mounted = true

    async function load() {
      try {
        if (mounted) {
          setLoading(true)
          setError(null)
        }

        const params = new URLSearchParams()
        if (bucket) params.set('bucket', bucket)
        if (workerId) params.set('workerId', workerId)
        if (filter) params.set('filter', filter)
        params.set('page', String(pageNumber))
        params.set('pageSize', '50')

        const [detail, entries] = await Promise.all([api.runDetail(runId), api.runEntries(runId, params)])

        if (mounted) {
          setRunDetail(detail)
          setEntriesResponse(entries)
          setLoading(false)
        }
      } catch (loadError) {
        if (mounted) {
          setLoading(false)
          setError(loadError instanceof Error ? loadError.message : 'Unable to load run detail.')
        }
      }
    }

    if (runId) {
      void load()
    }

    return () => {
      mounted = false
    }
  }, [runId, bucket, workerId, filter, pageNumber])

  const availableBuckets = useMemo(
    () =>
      Object.entries(runDetail?.bucketCounts ?? {})
        .filter(([, value]) => value > 0)
        .sort((a, b) => b[1] - a[1]),
    [runDetail],
  )

  const totalPages = useMemo(() => {
    if (!entriesResponse) return 1
    return Math.max(1, Math.ceil(entriesResponse.total / entriesResponse.pageSize))
  }, [entriesResponse])

  const selectedBucketCount = bucket ? runDetail?.bucketCounts[bucket] ?? 0 : entriesResponse?.total ?? 0
  const run = runDetail?.run ?? null
  const percentComplete = run ? progressPercent(run.processedWorkers, run.totalWorkers) : 0

  function setFilters(next: { bucket?: string; workerId?: string; filter?: string; page?: number }) {
    const params = new URLSearchParams(searchParams)

    if (next.bucket !== undefined) {
      if (next.bucket) params.set('bucket', next.bucket)
      else params.delete('bucket')
    }

    if (next.workerId !== undefined) {
      if (next.workerId) params.set('workerId', next.workerId)
      else params.delete('workerId')
    }

    if (next.filter !== undefined) {
      if (next.filter) params.set('filter', next.filter)
      else params.delete('filter')
    }

    if (next.page !== undefined) {
      params.set('page', String(next.page))
    }

    setSearchParams(params)
  }

  if (error) {
    return (
      <section className="vf-panel">
        <p className="vf-callout vf-callout-danger">{error}</p>
      </section>
    )
  }

  if (loading && !runDetail) {
    return (
      <section className="vf-panel">
        <p className="vf-muted-text">Loading run detail...</p>
      </section>
    )
  }

  if (!runDetail || !run) {
    return (
      <section className="vf-panel">
        <p className="vf-muted-text">Run not found.</p>
      </section>
    )
  }

  return (
    <>
      <section className="vf-hero vf-hero-compact">
        <div className="max-w-[54rem]">
          <p className="vf-eyebrow vf-eyebrow-dark">Run Investigation</p>
          <h1 className="vf-hero-title">{run.runId}</h1>
          <p className="vf-hero-lede">
            {run.mode} · {run.syncScope} · {run.status} · {run.dryRun ? 'Dry Run' : 'Live Run'}
          </p>
          <div className="vf-runtime-progress mt-6 max-w-[34rem]">
            <div className="vf-progress-row">
              <span>Worker progress</span>
              <strong className="text-white">{percentComplete}%</strong>
            </div>
            <div className="vf-progress-track">
              <span className="vf-progress-bar" style={{ width: `${percentComplete}%` }} />
            </div>
            <p className="text-[14px] leading-[1.43] tracking-[-0.016em] text-white/70">
              {run.processedWorkers} of {run.totalWorkers} workers processed · {formatDuration(run.durationSeconds)}
            </p>
          </div>
        </div>
        <div className="flex flex-wrap gap-3">
          <Link className="vf-secondary-button" to="/">
            Back to dashboard
          </Link>
          <Link className="vf-primary-link" to="/preview">
            Open preview
          </Link>
        </div>
      </section>

      <section className="vf-summary-grid">
        <article className="vf-stat-card">
          <p className="vf-stat-label">Status</p>
          <p className="vf-stat-value">{run.status}</p>
          <p className="vf-stat-meta">{run.runTrigger} · {run.requestedBy ?? 'Unassigned operator'}</p>
        </article>
        <article className="vf-stat-card">
          <p className="vf-stat-label">Duration</p>
          <p className="vf-stat-value">{formatDuration(run.durationSeconds)}</p>
          <p className="vf-stat-meta">{formatDate(run.startedAt)} to {formatDate(run.completedAt)}</p>
        </article>
        <article className="vf-stat-card">
          <p className="vf-stat-label">Materialized Summary</p>
          <p className="vf-stat-value">{entriesResponse?.total ?? 0}</p>
          <p className="vf-stat-meta">{runSummaryLine(run)}</p>
        </article>
      </section>

      <section className="vf-run-detail-grid">
        <div className="vf-panel-stack">
          <article className="vf-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">Run posture</p>
                <h2 className="vf-panel-title">Summary</h2>
              </div>
              <span className={`vf-badge ${statusTone(run.status)}`}>{run.status}</span>
            </div>

            <div className="vf-run-stats mt-5">
              <div className="vf-run-stat-row">
                <strong>Scope</strong>
                <span>{run.syncScope}</span>
              </div>
              <div className="vf-run-stat-row">
                <strong>Artifact</strong>
                <span>{run.artifactType}</span>
              </div>
              <div className="vf-run-stat-row">
                <strong>Requested by</strong>
                <span>{run.requestedBy ?? 'n/a'}</span>
              </div>
              <div className="vf-run-stat-row">
                <strong>Processed workers</strong>
                <span>{run.processedWorkers} / {run.totalWorkers}</span>
              </div>
            </div>

            <div className="vf-mini-grid">
              <dl className="vf-mini-card">
                <dt>Creates</dt>
                <dd>{run.creates}</dd>
              </dl>
              <dl className="vf-mini-card">
                <dt>Updates</dt>
                <dd>{run.updates}</dd>
              </dl>
              <dl className="vf-mini-card">
                <dt>Conflicts</dt>
                <dd>{run.conflicts}</dd>
              </dl>
              <dl className="vf-mini-card">
                <dt>Guardrails</dt>
                <dd>{run.guardrailFailures}</dd>
              </dl>
            </div>
          </article>

          <article className="vf-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">Entry surface</p>
                <h2 className="vf-panel-title">
                  Entries {bucket ? `· ${bucket}` : ''}
                </h2>
              </div>
              <span className="vf-caption">
                Page {entriesResponse?.page ?? 1} of {totalPages}
              </span>
            </div>

            <p className="vf-muted-text mt-3">
              {selectedBucketCount} matching entries · 50 per page. Expand an entry only when you
              need full attribute-level detail.
            </p>

            {!entriesResponse || entriesResponse.entries.length === 0 ? (
              <div className="vf-empty-state">No entries matched the current filter set.</div>
            ) : (
              <div className="vf-entry-shell mt-5">
                {entriesResponse.entries.map((entry) => {
                  const previewRunId = savedPreviewRunId(entry)
                  const hasRisk = Boolean(entry.failureSummary || entry.reviewCaseType || entry.reason)

                  return (
                    <article key={entry.entryId} className="vf-entry-card">
                      <header className="vf-entry-head">
                        <div>
                          <h3 className="vf-subtitle">{entry.bucketLabel}</h3>
                          <p className="vf-list-meta">
                            {entry.workerId ?? 'n/a'} / {entry.samAccountName ?? 'n/a'}
                          </p>
                        </div>
                        <div className="vf-entry-meta">
                          <span className={`vf-badge ${hasRisk ? 'warn' : 'neutral'}`}>
                            {entry.changeCount} changes
                          </span>
                        </div>
                      </header>

                      {entry.primarySummary ? (
                        <p className="vf-entry-summary">{entry.primarySummary}</p>
                      ) : null}

                      <div className="vf-entry-topline">
                        {entry.operationSummary ? (
                          <span className={`vf-badge ${hasRisk ? 'warn' : 'good'}`}>
                            {entry.operationSummary.action}
                          </span>
                        ) : null}
                        {entry.reviewCaseType ? (
                          <span className="vf-badge warn">{entry.reviewCaseType}</span>
                        ) : null}
                        {entry.reason ? <span className="vf-badge neutral">{entry.reason}</span> : null}
                      </div>

                      {entry.failureSummary ? (
                        <p className="vf-callout vf-callout-warn mt-4">{entry.failureSummary}</p>
                      ) : null}

                      {entry.topChangedAttributes.length > 0 ? (
                        <p className="vf-muted-text mt-4">
                          Top changes: {entry.topChangedAttributes.join(', ')}
                        </p>
                      ) : null}

                      {(previewRunId || entry.workerId) ? (
                        <div className="vf-entry-links">
                          {previewRunId ? (
                            <Link className="vf-inline-link" to={`/preview?runId=${previewRunId}`}>
                              Open saved preview <ArrowRight className="ml-1 inline size-4" />
                            </Link>
                          ) : null}
                          {!previewRunId && entry.workerId ? (
                            <Link className="vf-inline-link" to={`/preview?workerId=${entry.workerId}`}>
                              Open worker preview <ArrowRight className="ml-1 inline size-4" />
                            </Link>
                          ) : null}
                        </div>
                      ) : null}

                      <details className="vf-entry-details">
                        <summary>
                          <ChevronDown className="size-4" />
                          Inspect entry detail
                        </summary>

                        {entry.reviewCaseType || entry.reason ? (
                          <dl className="vf-kv">
                            <div><dt>Reason</dt><dd>{entry.reason ?? 'n/a'}</dd></div>
                            <div><dt>Review Case</dt><dd>{entry.reviewCaseType ?? 'n/a'}</dd></div>
                          </dl>
                        ) : null}

                        {entry.operationSummary ? (
                          <p className="mt-4 text-[17px] leading-[1.47] tracking-[-0.022em] text-[color:var(--vf-ink)]">
                            <strong>{entry.operationSummary.action}</strong>
                            {entry.operationSummary.effect ? ` · ${entry.operationSummary.effect}` : ''}
                          </p>
                        ) : null}

                        {entry.diffRows.length > 0 ? (
                          <div className="mt-5 overflow-x-auto">
                            <table className="vf-table vf-table-compact">
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
                          </div>
                        ) : (
                          <p className="vf-muted-text mt-4">
                            No detailed attribute diff was recorded for this entry.
                          </p>
                        )}
                      </details>
                    </article>
                  )
                })}
              </div>
            )}

            {entriesResponse && totalPages > 1 ? (
              <div className="vf-table-pagination">
                <p className="vf-muted-text">
                  Showing page {entriesResponse.page} of {totalPages}
                </p>
                <div className="vf-filter-actions">
                  {entriesResponse.page > 1 ? (
                    <button
                      className="vf-secondary-button"
                      type="button"
                      onClick={() => setFilters({ page: entriesResponse.page - 1 })}
                    >
                      Previous
                    </button>
                  ) : null}
                  {entriesResponse.page < totalPages ? (
                    <button
                      className="vf-secondary-button"
                      type="button"
                      onClick={() => setFilters({ page: entriesResponse.page + 1 })}
                    >
                      Next
                    </button>
                  ) : null}
                </div>
              </div>
            ) : null}
          </article>
        </div>

        <div className="vf-panel-stack">
          <article className="vf-panel vf-sticky-panel">
            <div className="vf-filters-panel">
              <div className="vf-section-heading">
                <div>
                  <p className="vf-panel-kicker">Investigation tools</p>
                  <h2 className="vf-panel-title">Filters</h2>
                </div>
                <Filter className="size-5 text-[color:var(--vf-neutral)]" />
              </div>

              <form
                className="vf-form-grid"
                onSubmit={(event) => {
                  event.preventDefault()
                  const form = new FormData(event.currentTarget)
                  setFilters({
                    workerId: String(form.get('workerId') ?? ''),
                    filter: String(form.get('filter') ?? ''),
                    page: 1,
                  })
                }}
              >
                <label>
                  <span>Worker search</span>
                  <input
                    className="vf-input"
                    defaultValue={workerId}
                    name="workerId"
                    placeholder="worker id or samAccountName"
                    type="text"
                  />
                </label>
                <label className="vf-filter-search">
                  <span>Text search</span>
                  <input
                    className="vf-input"
                    defaultValue={filter}
                    name="filter"
                    placeholder="reason, reviewCaseType, raw entry text..."
                    type="text"
                  />
                </label>
                <div className="vf-filter-actions">
                  <button className="vf-primary-button" type="submit">
                    Apply filters
                  </button>
                  <button
                    className="vf-secondary-button"
                    type="button"
                    onClick={() => setSearchParams(new URLSearchParams())}
                  >
                    Clear
                  </button>
                </div>
              </form>

              <div className="vf-list">
                <article className="vf-list-row">
                  <div>
                    <p className="vf-list-title">Focused bucket</p>
                    <p className="vf-list-meta">
                      {bucket ? `${bucket} selected` : 'All buckets visible'}
                    </p>
                  </div>
                  <ShieldAlert className="size-5 text-[color:var(--vf-warn)]" />
                </article>
                <article className="vf-list-row">
                  <div>
                    <p className="vf-list-title">Worker targeting</p>
                    <p className="vf-list-meta">
                      {workerId ? `Searching for "${workerId}"` : 'No worker search applied'}
                    </p>
                  </div>
                  <UserRoundSearch className="size-5 text-[color:var(--vf-neutral)]" />
                </article>
                <article className="vf-list-row">
                  <div>
                    <p className="vf-list-title">Free-text filter</p>
                    <p className="vf-list-meta">
                      {filter ? `Searching for "${filter}"` : 'No free-text filter applied'}
                    </p>
                  </div>
                  <Search className="size-5 text-[color:var(--vf-ink-faint)]" />
                </article>
                <article className="vf-list-row">
                  <div>
                    <p className="vf-list-title">Started</p>
                    <p className="vf-list-meta">{formatDate(run.startedAt)}</p>
                  </div>
                  <Clock3 className="size-5 text-[color:var(--vf-ink-faint)]" />
                </article>
              </div>

              <div>
                <p className="vf-panel-kicker">Bucket counts</p>
                <div className="vf-chip-row">
                  <button
                    className={`vf-chip-button${!bucket ? ' active' : ''}`}
                    type="button"
                    onClick={() => setFilters({ bucket: '', page: 1 })}
                  >
                    All buckets
                  </button>
                  {availableBuckets.map(([name, count]) => (
                    <button
                      key={name}
                      className={`vf-chip-button${bucket === name ? ' active' : ''}`}
                      type="button"
                      onClick={() => setFilters({ bucket: name, page: 1 })}
                    >
                      <span className="vf-bucket-name">{name}</span>
                      <strong>{count}</strong>
                    </button>
                  ))}
                </div>
              </div>
            </div>
          </article>
        </div>
      </section>
    </>
  )
}
