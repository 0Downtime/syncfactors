import { useEffect, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

import {
  ApplyResultDetails,
  DiagnosticsSection,
  SourceConfidenceSections,
} from '@/components/runtime-sections'
import { api } from '@/lib/api'
import { buildRiskCallouts, displayBool, enableTransition } from '@/lib/format'
import type { DirectoryCommandResult, WorkerPreviewHistoryItem, WorkerPreviewResult } from '@/lib/types'

export function PreviewPage(props: {
  onFlash: (flash: { tone: 'good' | 'danger' | 'warn'; message: string } | null) => void
}) {
  const [searchParams, setSearchParams] = useSearchParams()
  const [preview, setPreview] = useState<WorkerPreviewResult | null>(null)
  const [previousPreview, setPreviousPreview] = useState<WorkerPreviewResult | null>(null)
  const [history, setHistory] = useState<WorkerPreviewHistoryItem[]>([])
  const [applyResult, setApplyResult] = useState<DirectoryCommandResult | null>(null)
  const [acknowledgeRealSync, setAcknowledgeRealSync] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const runId = searchParams.get('runId')
  const workerId = searchParams.get('workerId')
  const showAllAttributes = searchParams.get('showAllAttributes') === 'true'

  useEffect(() => {
    let cancelled = false

    void (async () => {
      if (!runId && !workerId) {
        if (!cancelled) {
          setPreview(null)
          setHistory([])
          setPreviousPreview(null)
          setApplyResult(null)
          setError(null)
        }
        return
      }

      try {
        const nextPreview = runId ? await api.previewByRunId(runId) : await api.createPreview(workerId ?? '')
        const historyResponse = await api.previewHistory(nextPreview.workerId)
        const nextPrevious = nextPreview.previousRunId ? await api.previewByRunId(nextPreview.previousRunId) : null

        if (!cancelled) {
          setPreview(nextPreview)
          setHistory(historyResponse.previews)
          setPreviousPreview(nextPrevious)
          setApplyResult(null)
          setError(nextPreview.errorMessage)
        }
      } catch (loadError) {
        if (!cancelled) {
          setPreview(null)
          setHistory([])
          setPreviousPreview(null)
          setApplyResult(null)
          setError(loadError instanceof Error ? loadError.message : 'Failed to load preview.')
        }
      }
    })()

    return () => {
      cancelled = true
    }
  }, [runId, workerId])

  const visibleDiffRows = useMemo(
    () => (showAllAttributes ? preview?.diffRows ?? [] : (preview?.diffRows ?? []).filter((row) => row.changed)),
    [preview, showAllAttributes],
  )
  const riskCallouts = useMemo(() => (preview ? buildRiskCallouts(preview) : []), [preview])

  function updateParams(nextValues: { runId?: string; workerId?: string; showAllAttributes?: boolean }) {
    const next = new URLSearchParams()
    if (nextValues.runId) next.set('runId', nextValues.runId)
    if (nextValues.workerId) next.set('workerId', nextValues.workerId)
    if (nextValues.showAllAttributes) next.set('showAllAttributes', 'true')
    setSearchParams(next)
  }

  return (
    <>
      <section className="vf-hero vf-hero-compact">
        <div className="max-w-[54rem]">
          <p className="vf-eyebrow vf-eyebrow-dark">Staging Sync</p>
          <h1 className="vf-hero-title">Worker Preview</h1>
          <p className="vf-hero-lede">Enter a worker ID to fetch the worker from SuccessFactors, compare it to Active Directory, and inspect the staged operations before a real sync.</p>
        </div>
      </section>

      <section className="vf-panel">
        <form
          key={`${runId ?? 'worker'}:${workerId ?? ''}:${showAllAttributes ? 'all' : 'changed'}`}
          className="vf-form-grid"
          onSubmit={(event) => {
            event.preventDefault()
            const form = new FormData(event.currentTarget)
            const nextWorkerId = String(form.get('workerId') ?? '').trim()
            updateParams({ workerId: nextWorkerId || undefined, showAllAttributes })
          }}
        >
          <label>
            <span>Worker Id</span>
            <input className="vf-input" name="workerId" defaultValue={workerId ?? ''} placeholder="1000123" />
          </label>
          <label className="vf-checkbox-row">
            <span>Show all attributes</span>
            <input
              type="checkbox"
              checked={showAllAttributes}
              onChange={(event) =>
                updateParams({
                  runId: runId ?? undefined,
                  workerId: runId ? undefined : workerId || undefined,
                  showAllAttributes: event.target.checked,
                })
              }
            />
          </label>
          <div className="vf-filter-actions">
            <button className="vf-primary-button" type="submit">Preview worker</button>
          </div>
        </form>
      </section>

      {error ? <DiagnosticsSection message={error} /> : null}

      {applyResult ? (
        <DiagnosticsSection
          message={applyResult.message}
          tone={applyResult.succeeded ? 'vf-callout-good' : 'vf-callout-danger'}
        >
          <ApplyResultDetails result={applyResult} />
        </DiagnosticsSection>
      ) : null}

      {preview ? (
        <>
          <section className="vf-preview-grid">
            <article className="vf-panel">
              <div className="vf-section-heading">
                <div>
                  <p className="vf-panel-kicker">Decision summary</p>
                  <h2 className="vf-panel-title">Saved preview</h2>
                </div>
              </div>
              <p className="vf-muted-text mt-3">
                This preview was persisted as{' '}
                {preview.runId ? <Link className="vf-inline-link" to={`/runs/${preview.runId}`}>{preview.runId}</Link> : 'n/a'}.
              </p>
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
              {preview.operationSummary?.effect ? <p className="vf-callout mt-5">{preview.operationSummary.effect}</p> : null}
              {preview.reason || preview.reviewCaseType ? (
                <dl className="vf-kv mt-5">
                  <div><dt>Reason</dt><dd>{preview.reason ?? 'n/a'}</dd></div>
                  <div><dt>Review Case</dt><dd>{preview.reviewCaseType ?? 'n/a'}</dd></div>
                </dl>
              ) : null}
            </article>

            <article className="vf-panel">
              <div className="vf-section-heading">
                <div>
                  <p className="vf-panel-kicker">Apply guardrail</p>
                  <h2 className="vf-panel-title">Real sync to AD</h2>
                </div>
              </div>
              <p className="vf-muted-text mt-3">Applying uses the saved preview snapshot, not a silent re-run. This action writes to real Active Directory.</p>
              {!riskCallouts.length ? (
                <p className="vf-callout vf-callout-good mt-5">No high-risk fields changed in this preview.</p>
              ) : (
                <div className="vf-section-stack mt-5">
                  {riskCallouts.map((callout) => (
                    <p key={callout} className="vf-callout vf-callout-warn">{callout}</p>
                  ))}
                </div>
              )}
              <form
                className="vf-form-grid mt-5"
                onSubmit={(event) => {
                  event.preventDefault()
                  if (!preview.runId) {
                    return
                  }

                  void api
                    .applyPreview(preview.workerId, {
                      workerId: preview.workerId,
                      previewRunId: preview.runId,
                      previewFingerprint: preview.fingerprint,
                      acknowledgeRealSync,
                    })
                    .then((result) => {
                      setApplyResult(result)
                      props.onFlash({ tone: result.succeeded ? 'good' : 'danger', message: result.message })
                    })
                    .catch((applyError) => {
                      setError(applyError instanceof Error ? applyError.message : 'Failed to apply preview.')
                    })
                }}
              >
                <label className="vf-checkbox-row">
                  <span>I understand this will perform a real sync to AD using the saved preview.</span>
                  <input type="checkbox" checked={acknowledgeRealSync} onChange={(event) => setAcknowledgeRealSync(event.target.checked)} />
                </label>
                <p className="vf-callout vf-callout-warn">Real sync uses the reviewed preview fingerprint shown on this page and will fail if the saved snapshot is stale.</p>
                <div className="vf-filter-actions">
                  <button className="vf-primary-button" type="submit" disabled={!acknowledgeRealSync}>Real Sync To AD</button>
                </div>
              </form>
            </article>
          </section>

          {previousPreview ? (
            <section className="vf-panel">
              <div className="vf-section-heading">
                <div>
                  <p className="vf-panel-kicker">Previous comparison</p>
                  <h2 className="vf-panel-title">History delta</h2>
                </div>
              </div>
              <p className="vf-muted-text mt-3">
                Comparing this saved preview to{' '}
                {previousPreview.runId ? <Link className="vf-inline-link" to={`/runs/${previousPreview.runId}`}>{previousPreview.runId}</Link> : 'n/a'}.
              </p>
              <dl className="vf-kv mt-5">
                <div><dt>Previous Action</dt><dd>{previousPreview.operationSummary?.action ?? 'Unknown'}</dd></div>
                <div><dt>Previous Changes</dt><dd>{previousPreview.diffRows.filter((row) => row.changed).length}</dd></div>
                <div><dt>Plan Changed</dt><dd>{preview.fingerprint === previousPreview.fingerprint ? 'No' : 'Yes'}</dd></div>
              </dl>
              {preview.fingerprint !== previousPreview.fingerprint ? (
                <p className="vf-callout vf-callout-warn mt-5">The planned action or changed attributes differ from the previous saved preview for this worker.</p>
              ) : null}
            </section>
          ) : null}

          {history.length > 1 ? (
            <section className="vf-panel">
              <div className="vf-section-heading">
                <div>
                  <p className="vf-panel-kicker">Preview history</p>
                  <h2 className="vf-panel-title">Saved snapshots</h2>
                </div>
              </div>
              <div className="mt-5 overflow-x-auto">
                <table className="vf-data-table">
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
                        <td>{new Date(item.startedAt).toLocaleString()}</td>
                        <td>
                          <button className="vf-inline-link" type="button" onClick={() => updateParams({ runId: item.runId, showAllAttributes })}>
                            {item.runId}
                          </button>
                        </td>
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
                <p className="vf-panel-kicker">Attribute diff</p>
                <h2 className="vf-panel-title">Planned changes</h2>
              </div>
              <button
                className="vf-secondary-button"
                type="button"
                onClick={() =>
                  updateParams({
                    runId: preview.runId ?? undefined,
                    workerId: preview.runId ? undefined : preview.workerId,
                    showAllAttributes: !showAllAttributes,
                  })
                }
              >
                {showAllAttributes ? 'Show changed only' : 'Show all attributes'}
              </button>
            </div>
            {!visibleDiffRows.length ? (
              <p className="vf-muted-text mt-5">No synced attributes were recorded for this preview.</p>
            ) : (
              <div className="mt-5 overflow-x-auto">
                <table className="vf-data-table">
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
                        <td>{row.source ?? 'n/a'}</td>
                        <td>{row.changed ? 'Changed' : 'Same'}</td>
                        <td>{row.before}</td>
                        <td>{row.after}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>

          <SourceConfidenceSections preview={preview} />
        </>
      ) : null}
    </>
  )
}
