import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'

import { parseFailureDiagnostics } from '@/lib/diagnostics'
import { isPathLikeAttribute } from '@/lib/format'
import type { DirectoryCommandResult, RunEntry, WorkerPreviewResult } from '@/lib/types'

export function DiagnosticsSection(props: {
  message: string | null | undefined
  tone?: 'vf-callout-danger' | 'vf-callout-warn' | 'vf-callout-good'
  children?: ReactNode
}) {
  const diagnostics = parseFailureDiagnostics(props.message)
  if (!props.message) {
    return null
  }

  return (
    <section className="vf-panel">
      <p className={`vf-callout ${props.tone ?? 'vf-callout-danger'}`}>{props.message}</p>
      {diagnostics ? (
        <dl className="vf-kv mt-5">
          {diagnostics.details.map((item) => (
            <div key={`${item.label}-${item.value}`}>
              <dt>{item.label}</dt>
              <dd>{item.value}</dd>
            </div>
          ))}
          {diagnostics.guidance ? (
            <div>
              <dt>Next Check</dt>
              <dd>{diagnostics.guidance}</dd>
            </div>
          ) : null}
        </dl>
      ) : null}
      {props.children}
    </section>
  )
}

export function ApplyResultDetails(props: { result: DirectoryCommandResult }) {
  return (
    <dl className="vf-kv mt-5">
      <div><dt>Action</dt><dd>{props.result.action}</dd></div>
      <div><dt>SAM</dt><dd>{props.result.samAccountName}</dd></div>
      <div><dt>Distinguished Name</dt><dd>{props.result.distinguishedName ?? 'n/a'}</dd></div>
      <div>
        <dt>Run</dt>
        <dd>
          {props.result.runId ? <Link className="vf-inline-link" to={`/runs/${props.result.runId}`}>{props.result.runId}</Link> : 'n/a'}
        </dd>
      </div>
    </dl>
  )
}

export function RunEntryDiagnostics(props: { entry: RunEntry }) {
  const diagnostics = parseFailureDiagnostics(props.entry.reason ?? props.entry.failureSummary)
  if (!diagnostics || (!diagnostics.details.length && !diagnostics.guidance)) {
    return null
  }

  return (
    <dl className="vf-kv mt-5">
      {diagnostics.details.map((item) => (
        <div key={`${props.entry.entryId}-${item.label}-${item.value}`}>
          <dt>{item.label}</dt>
          <dd>{item.value}</dd>
        </div>
      ))}
      {diagnostics.guidance ? (
        <div>
          <dt>Next Check</dt>
          <dd>{diagnostics.guidance}</dd>
        </div>
      ) : null}
    </dl>
  )
}

export function SourceConfidenceSections(props: { preview: WorkerPreviewResult }) {
  const sourcePathAttributes = props.preview.sourceAttributes.filter((attribute) =>
    isPathLikeAttribute(attribute.attribute),
  )

  return (
    <>
      <section className="vf-preview-grid">
        <article className="vf-panel">
          <div className="vf-section-heading">
            <div>
              <p className="vf-panel-kicker">Source confidence</p>
              <h2 className="vf-panel-title">Mapped source inputs</h2>
            </div>
          </div>

          <div className="vf-section-stack mt-5">
            <div>
              <p className="vf-list-title">Used in sync</p>
              {!props.preview.usedSourceAttributes.length ? (
                <p className="vf-muted-text">No populated source attributes were referenced by the current diff.</p>
              ) : (
                <div className="mt-3 overflow-x-auto">
                  <table className="vf-data-table">
                    <thead><tr><th>Attribute</th><th>Value</th></tr></thead>
                    <tbody>
                      {props.preview.usedSourceAttributes.map((row) => (
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
              <p className="vf-list-title">Available but unused</p>
              {!props.preview.unusedSourceAttributes.length ? (
                <p className="vf-muted-text">No additional populated source values were captured.</p>
              ) : (
                <div className="mt-3 overflow-x-auto">
                  <table className="vf-data-table">
                    <thead><tr><th>Attribute</th><th>Value</th></tr></thead>
                    <tbody>
                      {props.preview.unusedSourceAttributes.slice(0, 20).map((row) => (
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
              <p className="vf-list-title">Missing expected fields</p>
              {!props.preview.missingSourceAttributes.length ? (
                <p className="vf-muted-text">No required mapped source fields are currently missing.</p>
              ) : (
                <ul className="vf-plain-list">
                  {props.preview.missingSourceAttributes.map((row) => (
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
          <div className="vf-section-heading">
            <div>
              <p className="vf-panel-kicker">Raw paths</p>
              <h2 className="vf-panel-title">Source aliases</h2>
            </div>
          </div>

          {!sourcePathAttributes.length ? (
            <p className="vf-muted-text mt-5">No raw source-path aliases were captured.</p>
          ) : (
            <div className="mt-5 overflow-x-auto">
              <table className="vf-data-table">
                <thead><tr><th>Source Path</th><th>Value</th></tr></thead>
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
        <div className="vf-section-heading">
          <div>
            <p className="vf-panel-kicker">Artifacts</p>
            <h2 className="vf-panel-title">Preview entries</h2>
          </div>
        </div>

        <div className="vf-entry-list mt-5">
          {props.preview.entries.map((entry, index) => (
            <article className="vf-entry-card" key={`${entry.bucket}-${index}`}>
              <header className="vf-entry-head">
                <div>
                  <h3>{entry.bucket}</h3>
                  <p className="vf-muted-text">Captured in saved preview payload</p>
                </div>
              </header>
              <pre className="vf-json-block">{JSON.stringify(entry.item, null, 2)}</pre>
            </article>
          ))}
        </div>
      </section>
    </>
  )
}
