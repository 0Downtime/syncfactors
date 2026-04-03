import { useEffect, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

import { api } from '@/lib/api'
import {
  buildRiskCallouts,
  displayBool,
  enableTransition,
  formatDate,
  isPathLikeAttribute,
} from '@/lib/format'
import type { DirectoryCommandResult, WorkerPreviewHistoryItem, WorkerPreviewResult } from '@/lib/types'

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

        const currentPreview = runId
          ? await api.previewByRunId(runId)
          : await api.previewByWorkerId(workerId!)

        const [historyResponse, previous] = await Promise.all([
          api.previewHistory(currentPreview.workerId),
          currentPreview.previousRunId
            ? api.previewByRunId(currentPreview.previousRunId)
            : Promise.resolve(null),
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
        <div>
          <p className="vf-eyebrow vf-eyebrow-dark">Staging Sync</p>
          <h1 className="vf-hero-title">Worker Preview</h1>
          <p className="vf-hero-lede">
            Enter a worker ID to fetch the worker from SuccessFactors, compare it to Active Directory,
            and inspect the staged operations before a real sync.
          </p>
        </div>
      </section>

      <section className="vf-panel">
        <form
          key={`${workerId ?? ''}-${showAllAttributes}`}
          className="vf-form-grid vf-preview-form"
          onSubmit={handleSubmit}
        >
          <label className="vf-filter-search">
            <span>Worker Id</span>
            <input
              className="vf-input"
              defaultValue={workerId ?? ''}
              name="workerId"
              placeholder="1000123"
              type="text"
            />
          </label>
          <label className="vf-checkbox-row">
            <span>Show all attributes</span>
            <input
              checked={showAllAttributes}
              type="checkbox"
              onChange={(event) => toggleShowAllAttributes(event.target.checked)}
            />
          </label>
          <div className="vf-filter-actions">
            <button className="vf-primary-button" type="submit">
              {loading ? 'Looking up...' : 'Preview Worker'}
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
          <section className="vf-panel-grid vf-preview-summary-grid">
            <article className="vf-panel">
              <div className="vf-section-heading">
                <div>
                  <h2 className="vf-panel-title">Decision Summary</h2>
                  <p className="mt-2 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
                    This preview was persisted as{' '}
                    {preview.runId ? (
                      <Link className="vf-inline-link" to={`/runs/${preview.runId}`}>
                        {preview.runId}
                      </Link>
                    ) : (
                      'n/a'
                    )}
                    .
                  </p>
                </div>
                <div className="vf-chip-row">
                  {preview.buckets.map((bucket) => (
                    <span key={bucket} className="vf-chip">{bucket}</span>
                  ))}
                </div>
              </div>

              <dl className="vf-kv mt-5">
                <div><dt>Action</dt><dd>{preview.operationSummary?.action ?? 'No operation'}</dd></div>
                <div><dt>Changed Attributes</dt><dd>{preview.diffRows.filter((row) => row.changed).length}</dd></div>
                <div><dt>Target OU</dt><dd>{preview.targetOu ?? 'n/a'}</dd></div>
                <div><dt>Manager</dt><dd>{preview.managerDistinguishedName ?? 'Unresolved'}</dd></div>
                <div><dt>Enable State</dt><dd>{enableTransition(preview.currentEnabled, preview.proposedEnable)}</dd></div>
                <div><dt>Matched Existing User</dt><dd>{displayBool(preview.matchedExistingUser)}</dd></div>
                <div><dt>SAM</dt><dd>{preview.samAccountName ?? 'n/a'}</dd></div>
                <div><dt>Current DN</dt><dd>{preview.currentDistinguishedName ?? 'n/a'}</dd></div>
              </dl>

              {preview.operationSummary?.effect ? (
                <p className="vf-callout mt-5">{preview.operationSummary.effect}</p>
              ) : null}

              {preview.reason || preview.reviewCaseType ? (
                <dl className="vf-kv mt-5">
                  <div><dt>Reason</dt><dd>{preview.reason ?? 'n/a'}</dd></div>
                  <div><dt>Review Case</dt><dd>{preview.reviewCaseType ?? 'n/a'}</dd></div>
                </dl>
              ) : null}
            </article>

            <article className="vf-panel">
              <h2 className="vf-panel-title">Apply Guardrail</h2>
              <p className="mt-4 text-[17px] leading-[1.47] tracking-[-0.022em] text-black/62">
                Applying uses the saved preview snapshot, not a silent re-run. This action writes to real Active Directory.
              </p>
              {riskCallouts.length === 0 ? (
                <p className="vf-callout vf-callout-good mt-5">No high-risk fields changed in this preview.</p>
              ) : (
                <div className="mt-5 grid gap-3">
                  {riskCallouts.map((callout) => (
                    <p key={callout} className="vf-callout vf-callout-warn">{callout}</p>
                  ))}
                </div>
              )}
              <div className="mt-5 grid gap-4">
                <label className="vf-checkbox-row">
                  <span>I understand this will perform a real sync to AD using the saved preview.</span>
                  <input
                    checked={acknowledgeRealSync}
                    type="checkbox"
                    onChange={(event) => setAcknowledgeRealSync(event.target.checked)}
                  />
                </label>
                <p className="vf-callout vf-callout-warn">
                  Real sync uses the reviewed preview fingerprint shown in this page and will fail if the saved snapshot is stale.
                </p>
                <div className="vf-filter-actions">
                  <button className="vf-primary-button" type="button" onClick={handleApply}>
                    Real Sync To AD
                  </button>
                </div>
              </div>
            </article>
          </section>

          {previousPreview ? (
            <section className="vf-panel">
              <h2 className="vf-panel-title">Previous Preview Comparison</h2>
              <p className="mt-2 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
                Comparing this saved preview to{' '}
                <Link className="vf-inline-link" to={`/preview?runId=${previousPreview.runId}`}>
                  {previousPreview.runId}
                </Link>
                .
              </p>
              <dl className="vf-kv mt-5">
                <div><dt>Previous Action</dt><dd>{previousPreview.operationSummary?.action ?? 'Unknown'}</dd></div>
                <div><dt>Previous Changes</dt><dd>{previousPreview.diffRows.filter((row) => row.changed).length}</dd></div>
                <div><dt>Plan Changed</dt><dd>{preview.fingerprint === previousPreview.fingerprint ? 'No' : 'Yes'}</dd></div>
              </dl>
              {preview.fingerprint !== previousPreview.fingerprint ? (
                <p className="vf-callout vf-callout-warn mt-5">
                  The planned action or changed attributes differ from the previous saved preview for this worker.
                </p>
              ) : null}
            </section>
          ) : null}

          {history.length > 1 ? (
            <section className="vf-panel">
              <h2 className="vf-panel-title">Preview History</h2>
              <div className="mt-5 overflow-x-auto">
                <table className="vf-table">
                  <thead>
                    <tr>
                      <th>Started</th>
                      <th>Run</th>
                      <th>Action</th>
                      <th>Status</th>
                      <th>Changes</th>
                      <th>Reason</th>
                    </tr>
                  </thead>
                  <tbody>
                    {history.map((item) => (
                      <tr key={item.runId}>
                        <td>{formatDate(item.startedAt)}</td>
                        <td><Link className="vf-inline-link" to={`/preview?runId=${item.runId}`}>{item.runId}</Link></td>
                        <td>{item.action ?? item.bucket}</td>
                        <td>{item.status ?? 'Unknown'}</td>
                        <td>{item.changeCount}</td>
                        <td>{item.reason ?? 'n/a'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          ) : null}

          <section className="vf-panel">
            <div className="vf-section-heading">
              <div>
                <h2 className="vf-panel-title">Attribute Diff</h2>
                <p className="mt-2 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
                  {showAllAttributes ? 'Showing every synced attribute.' : 'Showing changed attributes only.'}
                </p>
              </div>
              <div>
                {showAllAttributes ? (
                  <button className="vf-secondary-button" type="button" onClick={() => toggleShowAllAttributes(false)}>
                    Show changed only
                  </button>
                ) : (
                  <button className="vf-secondary-button" type="button" onClick={() => toggleShowAllAttributes(true)}>
                    Show all attributes
                  </button>
                )}
              </div>
            </div>

            {visibleDiffRows.length === 0 ? (
              <p className="mt-5 text-[17px] leading-[1.47] tracking-[-0.022em] text-black/62">
                No synced attributes were recorded for this preview.
              </p>
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
          </section>

          <section className="vf-panel-grid">
            <article className="vf-panel">
              <h2 className="vf-panel-title">Source Confidence</h2>
              <div className="mt-5 grid gap-6">
                <div>
                  <h3 className="vf-subtitle">Used In Sync</h3>
                  {preview.usedSourceAttributes.length === 0 ? (
                    <p className="mt-2 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
                      No populated source attributes were referenced by the current diff.
                    </p>
                  ) : (
                    <div className="mt-3 overflow-x-auto">
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
                </div>

                <div>
                  <h3 className="vf-subtitle">Available But Unused</h3>
                  {preview.unusedSourceAttributes.length === 0 ? (
                    <p className="mt-2 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
                      No additional populated source values were captured.
                    </p>
                  ) : (
                    <div className="mt-3 overflow-x-auto">
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
                </div>

                <div>
                  <h3 className="vf-subtitle">Missing Expected Fields</h3>
                  {preview.missingSourceAttributes.length === 0 ? (
                    <p className="mt-2 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
                      No required mapped source fields are currently missing.
                    </p>
                  ) : (
                    <ul className="vf-plain-list mt-3">
                      {preview.missingSourceAttributes.map((row) => (
                        <li key={`${row.attribute}-${row.reason}`}>
                          <code>{row.attribute}</code> · {row.reason}
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              </div>
            </article>

            <article className="vf-panel">
              <h2 className="vf-panel-title">Raw Source Paths</h2>
              {sourcePathAttributes.length === 0 ? (
                <p className="mt-5 text-[14px] leading-[1.43] tracking-[-0.016em] text-black/62">
                  No raw source-path aliases were captured.
                </p>
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
          </section>

          <section className="vf-panel">
            <h2 className="vf-panel-title">Preview Entries</h2>
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
          </section>
        </>
      ) : null}
    </>
  )
}
