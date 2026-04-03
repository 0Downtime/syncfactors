import { useEffect, useMemo, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'

import { api } from '@/lib/api'
import { formatDate, statusTone } from '@/lib/format'
import type { RunDetail, RunEntry } from '@/lib/types'

type EntriesResponse = {
  run: RunDetail['run']
  entries: RunEntry[]
  total: number
  page: number
  pageSize: number
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

        const [detail, entries] = await Promise.all([
          api.runDetail(runId),
          api.runEntries(runId, params),
        ])

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
        .map(([key]) => key),
    [runDetail],
  )

  const totalPages = useMemo(() => {
    if (!entriesResponse) return 1
    return Math.max(1, Math.ceil(entriesResponse.total / entriesResponse.pageSize))
  }, [entriesResponse])

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
        <p className="text-[17px] leading-[1.47] tracking-[-0.022em] text-black/62">Loading run detail...</p>
      </section>
    )
  }

  if (!runDetail) {
    return (
      <section className="vf-panel">
        <p className="text-[17px] leading-[1.47] tracking-[-0.022em] text-black/62">Run not found.</p>
      </section>
    )
  }

  return (
    <>
      <section className="vf-hero vf-hero-compact">
        <div>
          <p className="vf-eyebrow vf-eyebrow-dark">Run Detail</p>
          <h1 className="vf-hero-title">{runDetail.run.runId}</h1>
          <p className="vf-hero-lede">
            {runDetail.run.mode} · {runDetail.run.syncScope} · {runDetail.run.status} · {runDetail.run.dryRun ? 'Dry Run' : 'Live Run'}
          </p>
        </div>
        <div className="flex flex-wrap gap-3">
          <Link className="vf-primary-link" to="/">
            Back to dashboard
          </Link>
        </div>
      </section>

      <section className="vf-panel-grid">
        <article className="vf-panel">
          <h2 className="vf-panel-title">Summary</h2>
          <dl className="vf-kv mt-5">
            <div><dt>Started</dt><dd>{formatDate(runDetail.run.startedAt)}</dd></div>
            <div><dt>Completed</dt><dd>{formatDate(runDetail.run.completedAt)}</dd></div>
            <div><dt>Artifact</dt><dd>{runDetail.run.artifactType}</dd></div>
            <div><dt>Scope</dt><dd>{runDetail.run.syncScope}</dd></div>
            <div><dt>Trigger</dt><dd>{runDetail.run.runTrigger}</dd></div>
            <div><dt>Requested By</dt><dd>{runDetail.run.requestedBy ?? 'n/a'}</dd></div>
            <div><dt>Status</dt><dd><span className={`vf-badge ${statusTone(runDetail.run.status)}`}>{runDetail.run.status}</span></dd></div>
            <div><dt>Processed</dt><dd>{runDetail.run.processedWorkers}</dd></div>
            <div><dt>Total</dt><dd>{runDetail.run.totalWorkers}</dd></div>
          </dl>
        </article>

        <article className="vf-panel">
          <h2 className="vf-panel-title">Bucket Counts</h2>
          <div className="vf-bucket-grid mt-5">
            {Object.entries(runDetail.bucketCounts).map(([name, count]) => (
              <button
                key={name}
                className="vf-bucket-card text-left"
                type="button"
                onClick={() => setFilters({ bucket: name, page: 1 })}
              >
                <span className="vf-bucket-name">{name}</span>
                <strong>{count}</strong>
              </button>
            ))}
          </div>
        </article>
      </section>

      <section className="vf-panel">
        <h2 className="vf-panel-title">Filters</h2>
        <form
          className="vf-form-grid mt-5"
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
            <input className="vf-input" defaultValue={workerId} name="workerId" placeholder="worker id or samAccountName" type="text" />
          </label>
          <label className="vf-filter-search">
            <span>Text search</span>
            <input className="vf-input" defaultValue={filter} name="filter" placeholder="reason, reviewCaseType, raw entry text..." type="text" />
          </label>
          <div className="vf-filter-actions">
            <button className="vf-primary-button" type="submit">Apply</button>
            <button className="vf-secondary-button" type="button" onClick={() => setSearchParams(new URLSearchParams())}>Clear</button>
          </div>
        </form>
        <div className="vf-chip-row mt-5">
          <button className={`vf-chip-button${!bucket ? ' active' : ''}`} type="button" onClick={() => setFilters({ bucket: '', page: 1 })}>
            All buckets
          </button>
          {availableBuckets.map((name) => (
            <button
              key={name}
              className={`vf-chip-button${bucket === name ? ' active' : ''}`}
              type="button"
              onClick={() => setFilters({ bucket: name, page: 1 })}
            >
              <span className="vf-bucket-name">{name}</span>
              <strong>{runDetail.bucketCounts[name]}</strong>
            </button>
          ))}
        </div>
      </section>

      <section className="vf-panel">
        <h2 className="vf-panel-title">
          Entries {bucket ? `· ${bucket}` : ''}
        </h2>
        <p className="mt-3 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
          Showing page {entriesResponse?.page ?? 1} of {totalPages} · {entriesResponse?.total ?? 0} total entries · 50 per page.
        </p>
        {!entriesResponse || entriesResponse.entries.length === 0 ? (
          <p className="mt-5 text-[17px] leading-[1.47] tracking-[-0.022em] text-black/62">No entries matched this run.</p>
        ) : (
          <div className="vf-entry-list mt-5">
            {entriesResponse.entries.map((entry) => {
              const savedPreviewRunId =
                entry.artifactType === 'WorkerPreview'
                  ? entry.runId
                  : typeof entry.item.sourcePreviewRunId === 'string'
                    ? entry.item.sourcePreviewRunId
                    : null

              return (
                <article key={entry.entryId} className="vf-entry-card">
                  <header className="vf-entry-head">
                    <div>
                      <h3 className="vf-subtitle">{entry.bucketLabel}</h3>
                      <p className="mt-1 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
                        {entry.workerId ?? 'n/a'} / {entry.samAccountName ?? 'n/a'}
                      </p>
                    </div>
                    <div className="vf-entry-meta">
                      <span className="vf-badge neutral">{entry.changeCount} changes</span>
                    </div>
                  </header>
                  {entry.primarySummary ? <p className="vf-entry-summary">{entry.primarySummary}</p> : null}
                  {entry.failureSummary ? <p className="vf-callout vf-callout-warn">{entry.failureSummary}</p> : null}
                  {entry.reviewCaseType || entry.reason ? (
                    <dl className="vf-kv mt-5">
                      <div><dt>Reason</dt><dd>{entry.reason ?? 'n/a'}</dd></div>
                      <div><dt>Review Case</dt><dd>{entry.reviewCaseType ?? 'n/a'}</dd></div>
                    </dl>
                  ) : null}
                  {entry.operationSummary ? (
                    <p className="mt-4 text-[17px] leading-[1.47] tracking-[-0.022em] text-[#1d1d1f]">
                      <strong>{entry.operationSummary.action}</strong>
                      {entry.operationSummary.effect ? ` · ${entry.operationSummary.effect}` : ''}
                    </p>
                  ) : null}
                  {entry.topChangedAttributes.length > 0 ? (
                    <p className="mt-3 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
                      Top changes: {entry.topChangedAttributes.join(', ')}
                    </p>
                  ) : null}
                  {entry.workerId ? (
                    <p className="mt-3">
                      {savedPreviewRunId ? (
                        <Link className="vf-inline-link" to={`/preview?runId=${savedPreviewRunId}`}>
                          Open saved preview
                        </Link>
                      ) : (
                        <Link className="vf-inline-link" to={`/preview?workerId=${entry.workerId}`}>
                          Open worker preview
                        </Link>
                      )}
                    </p>
                  ) : null}
                  {entry.diffRows.length > 0 ? (
                    <div className="mt-5 overflow-x-auto">
                      <table className="vf-table vf-table-compact">
                        <thead>
                          <tr><th>Attribute</th><th>Before</th><th>After</th></tr>
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
                    <p className="mt-4 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
                      No detailed attribute diff was recorded for this entry.
                    </p>
                  )}
                </article>
              )
            })}
          </div>
        )}
        {entriesResponse && totalPages > 1 ? (
          <div className="vf-filter-actions mt-5">
            {entriesResponse.page > 1 ? (
              <button className="vf-secondary-button" type="button" onClick={() => setFilters({ page: entriesResponse.page - 1 })}>
                Previous
              </button>
            ) : null}
            {entriesResponse.page < totalPages ? (
              <button className="vf-secondary-button" type="button" onClick={() => setFilters({ page: entriesResponse.page + 1 })}>
                Next
              </button>
            ) : null}
          </div>
        ) : null}
      </section>
    </>
  )
}
