import type { ReactNode } from 'react';
import { parseFailureDiagnostics } from './diagnostics.js';
import type { DirectoryCommandResult, RunEntry, WorkerPreviewResult } from './types.js';

export function DiagnosticsSection(props: { message: string | null | undefined; tone?: 'danger' | 'warn' | 'good'; children?: ReactNode }) {
  const diagnostics = parseFailureDiagnostics(props.message);
  if (!props.message) {
    return null;
  }

  return (
    <section className="panel">
      <p className={`callout ${props.tone ?? 'danger'}`}>{props.message}</p>
      {diagnostics ? (
        <dl className="kv preview-meta">
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
  );
}

export function ApplyResultDetails(props: { result: DirectoryCommandResult }) {
  return (
    <dl className="kv preview-meta">
      <div><dt>Action</dt><dd>{props.result.action}</dd></div>
      <div><dt>SAM</dt><dd>{props.result.samAccountName}</dd></div>
      <div><dt>Distinguished Name</dt><dd>{props.result.distinguishedName ?? 'n/a'}</dd></div>
      <div><dt>Run</dt><dd>{props.result.runId ?? 'n/a'}</dd></div>
    </dl>
  );
}

export function SourceConfidenceSections(props: { preview: WorkerPreviewResult }) {
  const sourcePathAttributes = props.preview.sourceAttributes.filter((attribute) => isPathLikeAttribute(attribute.attribute));

  return (
    <>
      <section className="panel-grid">
        <article className="panel">
          <h2>Source Confidence</h2>
          <div className="source-confidence-stack">
            <div>
              <h3>Used In Sync</h3>
              {!props.preview.usedSourceAttributes.length ? (
                <p className="muted">No populated source attributes were referenced by the current diff.</p>
              ) : (
                <div className="table-scroll">
                  <table className="data-table compact">
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
              <h3>Available But Unused</h3>
              {!props.preview.unusedSourceAttributes.length ? (
                <p className="muted">No additional populated source values were captured.</p>
              ) : (
                <div className="table-scroll">
                  <table className="data-table compact">
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
              <h3>Missing Expected Fields</h3>
              {!props.preview.missingSourceAttributes.length ? (
                <p className="muted">No required mapped source fields are currently missing.</p>
              ) : (
                <ul className="plain-list">
                  {props.preview.missingSourceAttributes.map((row) => (
                    <li key={`${row.attribute}-${row.reason}`}><code>{row.attribute}</code> · {row.reason}</li>
                  ))}
                </ul>
              )}
            </div>
          </div>
        </article>

        <article className="panel">
          <h2>Raw Source Paths</h2>
          {!sourcePathAttributes.length ? (
            <p className="muted">No raw source-path aliases were captured.</p>
          ) : (
            <div className="table-scroll">
              <table className="data-table compact preview-source-table">
                <thead>
                  <tr>
                    <th>Source Path</th>
                    <th>Value</th>
                  </tr>
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

      <section className="panel">
        <h2>Preview Entries</h2>
        <div className="entry-list">
          {props.preview.entries.map((entry, index) => (
            <article className="entry-card" key={`${entry.bucket}-${index}`}>
              <header className="entry-head">
                <div><h3>{entry.bucket}</h3></div>
              </header>
              <pre className="json-block">{JSON.stringify(entry.item, null, 2)}</pre>
            </article>
          ))}
        </div>
      </section>
    </>
  );
}

export function RunEntryDiagnostics(props: { entry: RunEntry }) {
  const diagnostics = parseFailureDiagnostics(props.entry.reason ?? props.entry.failureSummary);
  if (!diagnostics || (!diagnostics.details.length && !diagnostics.guidance)) {
    return null;
  }

  return (
    <dl className="kv preview-meta">
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
  );
}

function isPathLikeAttribute(attribute: string) {
  return attribute.includes('[') || attribute.includes('.');
}
