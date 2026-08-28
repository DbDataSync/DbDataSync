import { useState } from 'react'
import { Field } from '../../components/Field'
import { useConnections, useTestScript } from '../../api/hooks'
import type { ScriptDefinition } from '../../api/types'

/**
 * Runs the script in the editor against sample input and shows what it did.
 *
 * Compiling proves a script is C#. It proves nothing about whether it produces the SQL, the value or
 * the rows the operator meant — and for a change query in particular, being wrong is not a crash but
 * a target that silently disagrees with its source.
 *
 * **Generated input by default; live only by explicit choice.** The connection picker starts empty
 * and choosing one is a deliberate act, because the alternative — a silent fallback to querying a
 * real system — is exactly the thing worth not doing. The result says which it was, every time.
 */
export function ScriptTestPanel({ draft }: { draft: ScriptDefinition }) {
  const { data: connections } = useConnections()
  const test = useTestScript()
  const [connectionName, setConnectionName] = useState('')

  const result = test.data

  return (
    <div className="card" data-testid="script-test-panel">
      <div className="card-head">
        <span className="card-title">Test</span>
        <span className="card-note">
          runs this script against sample input — compiling only proves it is C#
        </span>
      </div>

      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        <div className="form-grid">
          <Field label="Sample data">
            <select
              className="select"
              value={connectionName}
              onChange={(e) => setConnectionName(e.target.value)}
              data-testid="script-test-connection-select"
            >
              <option value="">Generated — no database is touched</option>
              {connections?.map((c) => (
                <option key={c.name} value={c.name}>Live rows from {c.name}</option>
              ))}
            </select>
          </Field>

          <Field label="&nbsp;">
            <button
              type="button"
              className="btn"
              disabled={test.isPending || !draft.manifest.entryType}
              onClick={() => test.mutate({
                name: draft.manifest.name || 'draft',
                request: { script: draft, connectionName: connectionName || undefined },
              })}
              data-testid="run-script-test-button"
            >
              {test.isPending ? 'Running…' : 'Run test'}
            </button>
          </Field>
        </div>

        {result && (
          <>
            {/* Said every time, and said first. Which system the rows came from is the one thing an
                operator must not have to infer. */}
            <span
              className={`badge ${result.mode === 'live' ? 'badge-accent' : ''}`}
              data-testid="script-test-source"
            >
              {result.source}
            </span>

            {result.error && (
              <div className="banner warn" role="alert" data-testid="script-test-error">
                {result.error}
              </div>
            )}

            {result.statement && (
              <pre className="mono" style={{ background: 'var(--sunken)', padding: 10, borderRadius: 6, margin: 0, overflowX: 'auto' }}>
                {result.statement}
              </pre>
            )}

            {result.cases.length > 0 && (
              <div className="card flush" data-testid="script-test-cases">
                <div className="grid-head" style={{ gridTemplateColumns: '1.4fr 1.4fr .8fr', gap: 14 }}>
                  <span>Input</span><span>Output</span><span />
                </div>
                {result.cases.map((c, i) => (
                  <div key={i} className="grid-row" style={{ gridTemplateColumns: '1.4fr 1.4fr .8fr', gap: 14 }}>
                    <span className="mono">{c.input}</span>
                    <span className="mono">{c.output ?? '—'}</span>
                    <span className="dim">{c.note ?? ''}</span>
                  </div>
                ))}
              </div>
            )}

            {result.log.length > 0 && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
                {result.log.map((line, i) => (
                  <span key={i} className="mono" style={{ font: '400 11.5px var(--mono)', color: 'var(--ink-4)' }}>{line}</span>
                ))}
              </div>
            )}
          </>
        )}
      </div>
    </div>
  )
}
