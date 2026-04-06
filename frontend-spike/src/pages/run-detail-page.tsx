import { useEffect, useMemo, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'

import { RunEntryDiagnostics } from '@/components/runtime-sections'
import { api } from '@/lib/api'
import { formatDate, getSavedPreviewRunId, runSummaryLine, statusTone } from '@/lib/format'
import type { RunDetail, RunEntriesResponse } from '@/lib/types'

const ENTRY_PAGE_SIZE = 50

export function RunDetailPage() {
  const { runId = '' } = useParams()
  const navigate = useNavigate()
  const [searchParams, setSearchParams] = useSearchParams()
  const [run, setRun] = useState<RunDetail | null>(null)
  const [response, setResponse] = useState<RunEntriesResponse | null>(null)
  const [error, setError] = useState<string | null>(null)

  const bucket = searchParams.get('bucket') ?? ''
  const workerId = searchParams.get('workerId') ?? ''
  const filter = searchParams.get('filter') ?? ''
  const page = Math.max(1, Number(searchParams.get('page') ?? '1') || 1)

  useEffect(() => {
    let cancelled = false

    void (async () => {
      try {
        const [runDetail, entriesResponse] = await Promise.all([
          api.runDetail(runId),
          api.runEntries(runId, {
            bucket: bucket || undefined,
            workerId: workerId || undefined,
            filter: filter || undefined,
            page,
            pageSize: ENTRY_PAGE_SIZE,
          }),
        ])
        if (!cancelled) {
          setRun(runDetail)
          setResponse(entriesResponse)
          setError(null)
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load run detail.')
        }
      }
    })()

    return () => {
      cancelled = true
    }
  }, [runId, bucket, workerId, filter, page])

  const availableBuckets = useMemo(
    () => Object.entries(run?.bucketCounts ?? {}).filter(([, count]) => count > 0),
    [run],
  )
  const totalPages = Math.max(1, Math.ceil((response?.total ?? 0) / ENTRY_PAGE_SIZE))

  function updateFilters(next: { bucket?: string; workerId?: string; filter?: string; page?: number }) {
    const params = new URLSearchParams()
    const nextBucket = next.bucket ?? bucket
    const nextWorkerId = next.workerId ?? workerId
    const nextFilter = next.filter ?? filter
    const nextPage = next.page ?? page

    if (nextBucket) params.set('bucket', nextBucket)
    if (nextWorkerId) params.set('workerId', nextWorkerId)
    if (nextFilter) params.set('filter', nextFilter)
    if (nextPage > 1) params.set('page', String(nextPage))
    setSearchParams(params)
  }

  return (
    <>
      {error ? (
        <section className="vf-panel">
          <p className="vf-callout vf-callout-danger">{error}</p>
        </section>
      ) : null}
      {!run ? null : (
        <>
          <section className="vf-hero vf-hero-compact">
            <div className="max-w-[54rem]">
              <p className="vf-eyebrow vf-eyebrow-dark">Run detail</p>
              <h1 className="vf-hero-title">{run.run.runId}</h1>
              <p className="vf-hero-lede">{run.run.mode} · {run.run.syncScope} · {run.run.status} · {run.run.dryRun ? 'Dry Run' : 'Live Run'}</p>
            </div>
          </section>

          <section className="vf-preview-grid">
            <article className="vf-panel">
              <div className="vf-section-heading">
                <div>
                  <p className="vf-panel-kicker">Summary</p>
                  <h2 className="vf-panel-title">Run metadata</h2>
                </div>
                <span className={`vf-badge ${statusTone(run.run.status)}`}>{run.run.status}</span>
              </div>
              <dl className="vf-kv mt-5">
                <div><dt>Started</dt><dd>{formatDate(run.run.startedAt)}</dd></div>
                <div><dt>Completed</dt><dd>{formatDate(run.run.completedAt)}</dd></div>
                <div><dt>Artifact</dt><dd>{run.run.artifactType}</dd></div>
                <div><dt>Scope</dt><dd>{run.run.syncScope}</dd></div>
                <div><dt>Trigger</dt><dd>{run.run.runTrigger}</dd></div>
                <div><dt>Requested By</dt><dd>{run.run.requestedBy ?? 'n/a'}</dd></div>
                <div><dt>Processed</dt><dd>{run.run.processedWorkers}</dd></div>
                <div><dt>Total</dt><dd>{run.run.totalWorkers}</dd></div>
              </dl>
            </article>

            <article className="vf-panel">
              <div className="vf-section-heading">
                <div>
                  <p className="vf-panel-kicker">Bucket counts</p>
                  <h2 className="vf-panel-title">Materialized summary</h2>
                </div>
              </div>
              <p className="vf-muted-text mt-3">{runSummaryLine(run.run)}</p>
              <div className="vf-chip-row mt-5">
                {Object.entries(run.bucketCounts).map(([name, count]) => (
                  <button key={name} className="vf-chip" type="button" onClick={() => updateFilters({ bucket: name, page: 1 })}>
                    {name} ({count})
                  </button>
                ))}
              </div>
            </article>
          </section>

          <section className="vf-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">Filters</p>
                <h2 className="vf-panel-title">Entry search</h2>
              </div>
            </div>
            <form
              key={`${workerId}:${filter}`}
              className="vf-form-grid mt-5"
              onSubmit={(event) => {
                event.preventDefault()
                const form = new FormData(event.currentTarget)
                updateFilters({
                  workerId: String(form.get('workerId') ?? ''),
                  filter: String(form.get('filter') ?? ''),
                  page: 1,
                })
              }}
            >
              <label><span>Worker search</span><input className="vf-input" name="workerId" defaultValue={workerId} placeholder="worker id or samAccountName" /></label>
              <label><span>Text search</span><input className="vf-input" name="filter" defaultValue={filter} placeholder="reason, reviewCaseType, raw entry text..." /></label>
              <div className="vf-filter-actions">
                <button className="vf-primary-button" type="submit">Apply</button>
                <button className="vf-secondary-button" type="button" onClick={() => setSearchParams(new URLSearchParams())}>Clear</button>
              </div>
            </form>
            <div className="vf-chip-row mt-5">
              <button className="vf-chip" type="button" onClick={() => updateFilters({ bucket: '', page: 1 })}>All buckets</button>
              {availableBuckets.map(([name, count]) => (
                <button key={name} className="vf-chip" type="button" onClick={() => updateFilters({ bucket: name, page: 1 })}>
                  {name} ({count})
                </button>
              ))}
            </div>
          </section>

          <section className="vf-panel">
            <div className="vf-section-heading">
              <div>
                <p className="vf-panel-kicker">Entries</p>
                <h2 className="vf-panel-title">{bucket ? `Filtered by ${bucket}` : 'All entries'}</h2>
              </div>
            </div>
            <p className="vf-muted-text mt-3">Showing page {page} of {totalPages} · {response?.total ?? 0} total entries · {ENTRY_PAGE_SIZE} per page.</p>
            {!response?.entries.length ? (
              <p className="vf-muted-text mt-5">No entries matched this run.</p>
            ) : (
              <div className="vf-entry-list mt-5">
                {response.entries.map((entry) => {
                  const savedPreviewRunId = getSavedPreviewRunId(entry)
                  return (
                    <article className="vf-entry-card" key={entry.entryId}>
                      <header className="vf-entry-head">
                        <div>
                          <h3>{entry.bucketLabel}</h3>
                          <p className="vf-muted-text">{entry.workerId ?? 'n/a'} / {entry.samAccountName ?? 'n/a'}</p>
                        </div>
                        <div className="vf-inline-actions">
                          <span className="vf-badge neutral">{entry.changeCount} changes</span>
                        </div>
                      </header>
                      {entry.primarySummary ? <p className="mt-3">{entry.primarySummary}</p> : null}
                      {entry.failureSummary ? <p className="vf-callout vf-callout-warn mt-3">{entry.failureSummary}</p> : null}
                      {entry.reason || entry.reviewCaseType ? (
                        <>
                          <dl className="vf-kv mt-5">
                            <div><dt>Reason</dt><dd>{entry.reason ?? 'n/a'}</dd></div>
                            <div><dt>Review Case</dt><dd>{entry.reviewCaseType ?? 'n/a'}</dd></div>
                          </dl>
                          <RunEntryDiagnostics entry={entry} />
                        </>
                      ) : null}
                      {entry.operationSummary ? <p className="mt-3"><strong>{entry.operationSummary.action}</strong>{entry.operationSummary.effect ? ` · ${entry.operationSummary.effect}` : ''}</p> : null}
                      {entry.topChangedAttributes.length ? <p className="vf-muted-text mt-3">Top changes: {entry.topChangedAttributes.join(', ')}</p> : null}
                      {entry.workerId ? (
                        <p className="mt-3">
                          <button
                            className="vf-inline-link"
                            type="button"
                            onClick={() =>
                              navigate(
                                savedPreviewRunId
                                  ? `/preview?runId=${encodeURIComponent(savedPreviewRunId)}`
                                  : `/preview?workerId=${encodeURIComponent(entry.workerId!)}`,
                              )
                            }
                          >
                            {savedPreviewRunId ? 'Open saved preview' : 'Open worker preview'}
                          </button>
                        </p>
                      ) : null}
                      {entry.diffRows.length ? (
                        <div className="mt-5 overflow-x-auto">
                          <table className="vf-data-table">
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
                      ) : <p className="vf-muted-text mt-5">No detailed attribute diff was recorded for this entry.</p>}
                    </article>
                  )
                })}
              </div>
            )}

            {totalPages > 1 ? (
              <div className="vf-filter-actions mt-5">
                {page > 1 ? <button className="vf-secondary-button" type="button" onClick={() => updateFilters({ page: page - 1 })}>Previous</button> : null}
                {page < totalPages ? <button className="vf-secondary-button" type="button" onClick={() => updateFilters({ page: page + 1 })}>Next</button> : null}
              </div>
            ) : null}
          </section>
        </>
      )}
    </>
  )
}
