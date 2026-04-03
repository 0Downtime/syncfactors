import { useEffect, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import {
  AlertTriangle,
  ArrowRight,
  ChevronDown,
  FileSearch,
  History,
  ShieldCheck,
  UserRoundSearch,
} from 'lucide-react'

import { api } from '@/lib/api'
import {
  buildRiskCallouts,
  displayBool,
  enableTransition,
  formatDate,
  isPathLikeAttribute,
} from '@/lib/format'
import type { DirectoryCommandResult, WorkerPreviewHistoryItem, WorkerPreviewResult } from '@/lib/types'

function countChangedAttributes(preview: WorkerPreviewResult | null) {
  if (!preview) return 0
  return preview.diffRows.filter((row) => row.changed).length
}

export function PreviewPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const [acknowledgeRealSync, setAcknowledgeRealSync] = useState(false)
  const [preview, setPreview] = useState<WorkerPreviewResult | null>(null)
  const [previousPreview, setPreviousPreview] = useState<WorkerPreviewResult | null>(null)
  const [history, setHistory] = useState<WorkerPreviewHistoryItem[]>([])
  const [applyResult, setApplyResult] = useState<DirectoryCommandResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const runId = searchParams.get('runId')
  const workerId = searchParams.get('workerId')
  const showAllAttributes = searchParams.get('showAllAttributes') === 'true'

  useEffect(() => {
    let mounted = true

    async function load() {
      if (!runId && !workerId) {
        if (mounted) {
          setPreview(null)
          setPreviousPreview(null)
          setHistory([])
          setError(null)
          setApplyResult(null)
        }
        return
      }

      try {
        if (mounted) {
          setLoading(true)
          setError(null)
          setApplyResult(null)
        }

        const currentPreview = runId ? await api.previewByRunId(runId) : await api.previewByWorkerId(workerId!)

        const [historyResponse, previous] = await Promise.all([
          api.previewHistory(currentPreview.workerId),
          currentPreview.previousRunId ? api.previewByRunId(currentPreview.previousRunId) : Promise.resolve(null),
        ])

        if (mounted) {
          setPreview(currentPreview)
          setPreviousPreview(previous)
          setHistory(historyResponse.previews)
          setLoading(false)
        }
      } catch (loadError) {
        if (mounted) {
          setLoading(false)
          setPreview(null)
          setPreviousPreview(null)
          setHistory([])
          setError(loadError instanceof Error ? loadError.message : 'Unable to load preview.')
        }
      }
    }

    void load()
    return () => {
      mounted = false
    }
  }, [runId, workerId])

  const visibleDiffRows = useMemo(() => {
    if (!preview) return []
    return showAllAttributes ? preview.diffRows : preview.diffRows.filter((row) => row.changed)
  }, [preview, showAllAttributes])

  const sourcePathAttributes = useMemo(
    () => preview?.sourceAttributes.filter((attribute) => isPathLikeAttribute(attribute.attribute)) ?? [],
    [preview],
  )

  const riskCallouts = useMemo(() => (preview ? buildRiskCallouts(preview) : []), [preview])
  const changedAttributes = countChangedAttributes(preview)

  function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    const nextWorkerId = String(form.get('workerId') ?? '').trim()
    const next = new URLSearchParams()
    if (nextWorkerId) {
      next.set('workerId', nextWorkerId)
    }
    if (showAllAttributes) {
      next.set('showAllAttributes', 'true')
    }
    setSearchParams(next)
  }

  function toggleShowAllAttributes(nextValue: boolean) {
    const next = new URLSearchParams(searchParams)
    if (nextValue) {
      next.set('showAllAttributes', 'true')
    } else {
      next.delete('showAllAttributes')
    }
    setSearchParams(next)
  }

  async function handleApply() {
    if (!preview?.runId) {
      setError('Refresh preview before applying.')
      return
    }

    try {
      const result = await api.applyPreview(preview.workerId, {
        workerId: preview.workerId,
        previewRunId: preview.runId,
        previewFingerprint: preview.fingerprint,
        acknowledgeRealSync,
      })
      setApplyResult(result)
      setError(null)
    } catch (applyError) {
      setError(applyError instanceof Error ? applyError.message : 'Unable to apply preview.')
    }
  }

  return (
    <>
      <section className="vf-hero vf-hero-compact">
        <div className="max-w-[54rem]">
          <p className="vf-eyebrow vf-eyebrow-dark">Staged Review</p>
          <h1 className="vf-hero-title">Worker Preview</h1>
          <p className="vf-hero-lede">
            Pull a worker into a guided review flow, inspect change risk, compare against previous
            previews, and only then promote the saved snapshot into a real sync.
          </p>
          <div className="mt-6 flex flex-wrap gap-3">
            <Link className="vf-secondary-button" to="/">
              Back to dashboard
            </Link>
          </div>
        </div>

        <div className="w-full max-w-[360px] rounded-[28px] border border-white/12 bg-white/8 p-5 shadow-[0_18px_40px_rgba(0,0,0,0.18)] backdrop-blur-[18px]">
          <p className="vf-eyebrow vf-eyebrow-dark">Review posture</p>
          <p className="mt-2 text-[28px] font-semibold tracking-[-0.03em] text-white">
            {preview?.operationSummary?.action ?? 'Awaiting lookup'}
          </p>
          <div className="vf-mini-grid mt-5">
            <dl className="vf-mini-card">
              <dt>Changed fields</dt>
              <dd>{preview ? changedAttributes : '0'}</dd>
            </dl>
            <dl className="vf-mini-card">
              <dt>Risk callouts</dt>
              <dd>{riskCallouts.length}</dd>
            </dl>
          </div>
          <p className="mt-4 text-[14px] leading-[1.43] tracking-[-0.016em] text-white/72">
            {preview ? `Reviewing worker ${preview.workerId}` : 'Search for a worker or saved preview to begin.'}
          </p>
        </div>
      </section>

      <section className="vf-panel">
        <form key={`${workerId ?? ''}-${showAllAttributes}`} className="vf-form-grid vf-preview-form" onSubmit={handleSubmit}>
          <label className="vf-filter-search">
            <span>Worker Id</span>
            <input className="vf-input" defaultValue={workerId ?? ''} name="workerId" placeholder="1000123" type="text" />
          </label>
          <label className="vf-checkbox-row">
            <span>Show all attributes</span>
            <input checked={showAllAttributes} type="checkbox" onChange={(event) => toggleShowAllAttributes(event.target.checked)} />
          </label>
          <div className="vf-filter-actions">
            <button className="vf-primary-button" type="submit">
              {loading ? 'Looking up...' : 'Preview worker'}
            </button>
          </div>
        </form>
      </section>

      {error ? (
        <section className="vf-panel">
          <p className="vf-callout vf-callout-danger">{error}</p>
        </section>
      ) : null}

      {applyResult ? (
        <section className="vf-panel">
          <p className={`vf-callout ${applyResult.succeeded ? 'vf-callout-good' : 'vf-callout-danger'}`}>
            {applyResult.message}
          </p>
          <dl className="vf-kv mt-5">
            <div><dt>Action</dt><dd>{applyResult.action}</dd></div>
            <div><dt>SAM</dt><dd>{applyResult.samAccountName}</dd></div>
            <div><dt>Distinguished Name</dt><dd>{applyResult.distinguishedName ?? 'n/a'}</dd></div>
            <div><dt>Run</dt><dd>{applyResult.runId ? <Link className="vf-inline-link" to={`/runs/${applyResult.runId}`}>{applyResult.runId}</Link> : 'n/a'}</dd></div>
          </dl>
        </section>
      ) : null}

      {preview ? (
        <>
          <section className="vf-summary-grid">
            <article className="vf-stat-card">
              <p className="vf-stat-label">Planned action</p>
              <p className="vf-stat-value">{preview.operationSummary?.action ?? 'No operation'}</p>
              <p className="vf-stat-meta">{preview.operationSummary?.effect ?? 'No additional effect summary.'}</p>
            </article>
            <article className="vf-stat-card">
              <p className="vf-stat-label">Identity match</p>
              <p className="vf-stat-value">{preview.samAccountName ?? 'n/a'}</p>
              <p className="vf-stat-meta">{displayBool(preview.matchedExistingUser)} existing-user match</p>
            </article>
            <article className="vf-stat-card">
              <p className="vf-stat-label">Saved preview</p>
              <p className="vf-stat-value">{preview.runId ?? 'n/a'}</p>
              <p className="vf-stat-meta">{history.length} recent preview snapshots for this worker</p>
            </article>
          </section>

          <section className="vf-preview-grid">
            <div className="vf-section-stack">
              <article className="vf-panel">
                <div className="vf-section-heading">
                  <div>
                    <p className="vf-panel-kicker">Decision surface</p>
                    <h2 className="vf-panel-title">Decision summary</h2>
                  </div>
                  <div className="vf-chip-row">
                    {preview.buckets.map((bucket) => (
                      <span key={bucket} className="vf-chip">{bucket}</span>
                    ))}
                  </div>
                </div>

                <dl className="vf-kv mt-5">
                  <div><dt>Changed Attributes</dt><dd>{changedAttributes}</dd></div>
                  <div><dt>Target OU</dt><dd>{preview.targetOu ?? 'n/a'}</dd></div>
                  <div><dt>Manager</dt><dd>{preview.managerDistinguishedName ?? 'Unresolved'}</dd></div>
                  <div><dt>Enable State</dt><dd>{enableTransition(preview.currentEnabled, preview.proposedEnable)}</dd></div>
                  <div><dt>Matched Existing User</dt><dd>{displayBool(preview.matchedExistingUser)}</dd></div>
                  <div><dt>Current DN</dt><dd>{preview.currentDistinguishedName ?? 'n/a'}</dd></div>
                </dl>

                {preview.reason || preview.reviewCaseType ? (
                  <div className="vf-note-grid">
                    <div className="vf-command-card">
                      <p className="vf-panel-kicker">Operator reasoning</p>
                      <p className="vf-list-title">{preview.reviewCaseType ?? 'No review case type'}</p>
                      <p className="vf-muted-text">{preview.reason ?? 'No reason was attached to this preview.'}</p>
                    </div>
                  </div>
                ) : null}
              </article>

              <article className="vf-panel">
                <div className="vf-section-heading">
                  <div>
                    <p className="vf-panel-kicker">Attribute review</p>
                    <h2 className="vf-panel-title">Attribute diff</h2>
                  </div>
                  <button className="vf-secondary-button" type="button" onClick={() => toggleShowAllAttributes(!showAllAttributes)}>
                    {showAllAttributes ? 'Show changed only' : 'Show all attributes'}
                  </button>
                </div>

                <p className="vf-muted-text mt-3">
                  {showAllAttributes ? 'Showing every synced attribute.' : 'Showing changed attributes only.'}
                </p>

                {visibleDiffRows.length === 0 ? (
                  <div className="vf-empty-state">No synced attributes were recorded for this preview.</div>
                ) : (
                  <div className="mt-5 overflow-x-auto">
                    <table className="vf-table vf-diff-table">
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
                            <td>{row.source ?? '-'}</td>
                            <td><span className={`vf-badge ${row.changed ? 'good' : 'neutral'}`}>{row.changed ? 'Changed' : 'Unchanged'}</span></td>
                            <td>{row.before}</td>
                            <td>{row.after}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </article>

              <article className="vf-panel">
                <div className="vf-section-heading">
                  <div>
                    <p className="vf-panel-kicker">Traceability</p>
                    <h2 className="vf-panel-title">Source confidence</h2>
                  </div>
                  <FileSearch className="size-5 text-[color:var(--vf-neutral)]" />
                </div>

                <div className="vf-note-grid">
                  <details className="vf-entry-details" open>
                    <summary>
                      <ChevronDown className="size-4" />
                      Used in sync
                    </summary>
                    {preview.usedSourceAttributes.length === 0 ? (
                      <div className="vf-empty-state">No populated source attributes were referenced by the current diff.</div>
                    ) : (
                      <div className="overflow-x-auto">
                        <table className="vf-table vf-table-compact">
                          <thead><tr><th>Attribute</th><th>Value</th></tr></thead>
                          <tbody>
                            {preview.usedSourceAttributes.map((row) => (
                              <tr key={`${row.attribute}-${row.value}`}>
                                <td>{row.attribute}</td>
                                <td>{row.value}</td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>
                    )}
                  </details>

                  <details className="vf-entry-details">
                    <summary>
                      <ChevronDown className="size-4" />
                      Available but unused
                    </summary>
                    {preview.unusedSourceAttributes.length === 0 ? (
                      <div className="vf-empty-state">No additional populated source values were captured.</div>
                    ) : (
                      <div className="overflow-x-auto">
                        <table className="vf-table vf-table-compact">
                          <thead><tr><th>Attribute</th><th>Value</th></tr></thead>
                          <tbody>
                            {preview.unusedSourceAttributes.slice(0, 20).map((row) => (
                              <tr key={`${row.attribute}-${row.value}`}>
                                <td>{row.attribute}</td>
                                <td>{row.value}</td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>
                    )}
                  </details>

                  <details className="vf-entry-details">
                    <summary>
                      <ChevronDown className="size-4" />
                      Missing expected fields
                    </summary>
                    {preview.missingSourceAttributes.length === 0 ? (
                      <div className="vf-empty-state">No required mapped source fields are currently missing.</div>
                    ) : (
                      <ul className="vf-plain-list">
                        {preview.missingSourceAttributes.map((row) => (
                          <li key={`${row.attribute}-${row.reason}`}>
                            <code>{row.attribute}</code> · {row.reason}
                          </li>
                        ))}
                      </ul>
                    )}
                  </details>
                </div>
              </article>
            </div>

            <div className="vf-section-stack">
              <article className="vf-panel vf-sticky-panel">
                <div className="vf-section-heading">
                  <div>
                    <p className="vf-panel-kicker">Guardrail</p>
                    <h2 className="vf-panel-title">Apply review</h2>
                  </div>
                  <ShieldCheck className="size-5 text-[color:var(--vf-good)]" />
                </div>

                <p className="vf-muted-text mt-3">
                  Applying uses the saved preview snapshot, not a silent re-run. This action writes
                  to real Active Directory.
                </p>

                <div className="vf-risk-list">
                  {riskCallouts.length === 0 ? (
                    <p className="vf-callout vf-callout-good">No high-risk fields changed in this preview.</p>
                  ) : (
                    riskCallouts.map((callout) => (
                      <p key={callout} className="vf-callout vf-callout-warn">{callout}</p>
                    ))
                  )}
                </div>

                <div className="vf-note-grid">
                  <label className="vf-checkbox-row">
                    <span>I understand this will perform a real sync to AD using the saved preview.</span>
                    <input checked={acknowledgeRealSync} type="checkbox" onChange={(event) => setAcknowledgeRealSync(event.target.checked)} />
                  </label>
                  <p className="vf-callout vf-callout-warn">
                    Real sync uses the reviewed preview fingerprint shown in this page and will fail if the saved snapshot is stale.
                  </p>
                  <div className="vf-command-actions">
                    <button className="vf-primary-button" disabled={!acknowledgeRealSync} type="button" onClick={handleApply}>
                      Real Sync To AD
                    </button>
                    {preview.runId ? (
                      <Link className="vf-inline-link" to={`/runs/${preview.runId}`}>
                        Open saved preview run <ArrowRight className="ml-1 inline size-4" />
                      </Link>
                    ) : null}
                  </div>
                </div>
              </article>

              {previousPreview ? (
                <article className="vf-panel">
                  <div className="vf-section-heading">
                    <div>
                      <p className="vf-panel-kicker">Drift check</p>
                      <h2 className="vf-panel-title">Previous preview comparison</h2>
                    </div>
                    <History className="size-5 text-[color:var(--vf-neutral)]" />
                  </div>

                  <dl className="vf-kv mt-5">
                    <div><dt>Previous Action</dt><dd>{previousPreview.operationSummary?.action ?? 'Unknown'}</dd></div>
                    <div><dt>Previous Changes</dt><dd>{countChangedAttributes(previousPreview)}</dd></div>
                    <div><dt>Plan Changed</dt><dd>{preview.fingerprint === previousPreview.fingerprint ? 'No' : 'Yes'}</dd></div>
                  </dl>

                  <div className="vf-command-actions mt-5">
                    <Link className="vf-inline-link" to={`/preview?runId=${previousPreview.runId}`}>
                      Open previous preview <ArrowRight className="ml-1 inline size-4" />
                    </Link>
                  </div>

                  {preview.fingerprint !== previousPreview.fingerprint ? (
                    <p className="vf-callout vf-callout-warn mt-5">
                      The planned action or changed attributes differ from the previous saved preview for this worker.
                    </p>
                  ) : null}
                </article>
              ) : null}

              {history.length > 1 ? (
                <article className="vf-panel">
                  <div className="vf-section-heading">
                    <div>
                      <p className="vf-panel-kicker">Timeline</p>
                      <h2 className="vf-panel-title">Preview history</h2>
                    </div>
                    <UserRoundSearch className="size-5 text-[color:var(--vf-ink-faint)]" />
                  </div>

                  <div className="vf-list mt-5">
                    {history.map((item) => (
                      <article key={item.runId} className="vf-list-row">
                        <div>
                          <p className="vf-list-title">{item.action ?? item.bucket}</p>
                          <p className="vf-list-meta">
                            {formatDate(item.startedAt)} · {item.status ?? 'Unknown'} · {item.changeCount} changes
                          </p>
                          <p className="vf-list-meta">{item.reason ?? 'No reason attached.'}</p>
                        </div>
                        <Link className="vf-inline-link" to={`/preview?runId=${item.runId}`}>
                          Open <ArrowRight className="ml-1 inline size-4" />
                        </Link>
                      </article>
                    ))}
                  </div>
                </article>
              ) : null}
            </div>
          </section>

          <section className="vf-panel-grid">
            <article className="vf-panel">
              <div className="vf-section-heading">
                <div>
                  <p className="vf-panel-kicker">Source aliases</p>
                  <h2 className="vf-panel-title">Raw source paths</h2>
                </div>
                <AlertTriangle className="size-5 text-[color:var(--vf-warn)]" />
              </div>
              {sourcePathAttributes.length === 0 ? (
                <div className="vf-empty-state">No raw source-path aliases were captured.</div>
              ) : (
                <div className="mt-5 overflow-x-auto">
                  <table className="vf-table vf-table-compact">
                    <thead>
                      <tr><th>Source Path</th><th>Value</th></tr>
                    </thead>
                    <tbody>
                      {sourcePathAttributes.map((row) => (
                        <tr key={`${row.attribute}-${row.value}`}>
                          <td>{row.attribute}</td>
                          <td>{row.value}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </article>

            <article className="vf-panel">
              <div className="vf-section-heading">
                <div>
                  <p className="vf-panel-kicker">Raw artifacts</p>
                  <h2 className="vf-panel-title">Preview entries</h2>
                </div>
                <FileSearch className="size-5 text-[color:var(--vf-ink-faint)]" />
              </div>
              <div className="vf-entry-list mt-5">
                {preview.entries.map((entry, index) => (
                  <article key={`${entry.bucket}-${index}`} className="vf-entry-card">
                    <header className="vf-entry-head">
                      <h3 className="vf-subtitle">{entry.bucket}</h3>
                    </header>
                    <pre className="vf-json-block">{JSON.stringify(entry.item, null, 2)}</pre>
                  </article>
                ))}
              </div>
            </article>
          </section>
        </>
      ) : (
        <section className="vf-panel">
          <div className="vf-empty-state">
            Search for a worker ID or open a saved preview run to begin the review flow.
          </div>
        </section>
      )}
    </>
  )
}
