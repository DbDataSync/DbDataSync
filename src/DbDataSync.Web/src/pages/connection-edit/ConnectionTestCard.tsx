import { Field } from '../../components/Field'
import { useCredentialSource } from '../../api/hooks'
import type { ConnectionTestReport, LibraryValidationReport } from '../../api/types'

/**
 * The design's `Last test` card, plus the credential source the review asked for.
 *
 * "Last test" means the last test *this session performed* — nothing records a series, and a card
 * implying history the system does not keep would be worse than an empty one.
 *
 * Phase 109j: also shows the last "Validate library" run, when that action is offered at all
 * (`validate` undefined for a driver `supportsLibraryValidation` hides the button for entirely) — a
 * separate, slower, DDL-requiring action from Test, so its own outcome is shown separately rather than
 * folded into `TestResult`.
 */
export function ConnectionTestCard({ connectionName, report, pending, error, validate }: {
  connectionName: string | undefined
  report: ConnectionTestReport | undefined
  pending: boolean
  error: Error | null
  validate?: { report: LibraryValidationReport | undefined; pending: boolean; error: Error | null }
}) {
  return (
    <div className="card" data-testid="connection-test-card">
      <div className="card-head">
        <span className="card-title">Last test</span>
        <span className="card-note">this session only — nothing is recorded</span>
      </div>
      <div className="card-body">
        {pending && <span className="hint">Testing…</span>}
        {!pending && !report && !error && <span className="hint">Not tested yet.</span>}
        {!pending && error && <span className="hint" style={{ color: 'var(--danger)' }}>{error.message}</span>}
        {!pending && report && <TestResult report={report} />}
        <CredentialSourceFields connectionName={connectionName} />
        {validate && <LibraryValidationResult validate={validate} />}
      </div>
    </div>
  )
}

function TestResult({ report }: { report: ConnectionTestReport }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 9 }} data-testid="connection-test-result">
      <div className="row" style={{ gap: 8 }}>
        <span className="status">
          <span className={`dot ${report.succeeded ? 'dot-ok' : 'dot-bad'}`} />
          {report.succeeded ? 'reachable' : 'unreachable'}
        </span>
        <span className="dim">
          {Math.round(report.connectMs)}ms connect
          {report.succeeded && ` · ${Math.round(report.probeMs)}ms probe`}
        </span>
      </div>
      {report.serverVersion && (
        <span className="dim" style={{ lineHeight: 1.5 }}>{report.serverVersion}</span>
      )}
      {report.error && (
        // The provider's own wording, unedited: this is an operator console, and that message is most
        // of the diagnosis.
        <span style={{ color: 'var(--danger)', lineHeight: 1.5 }} data-testid="connection-test-error">{report.error}</span>
      )}
      {/* Phase 109j item 5: the static compatibility check's result, surfaced through the flow an
          operator already checks — "Connected, but the installed X is missing N members…". */}
      {report.libraryWarning && (
        <span style={{ color: 'var(--warning, #a66a00)', lineHeight: 1.5 }} data-testid="connection-library-warning">
          {report.libraryWarning}
        </span>
      )}
    </div>
  )
}

function LibraryValidationResult({ validate }: {
  validate: { report: LibraryValidationReport | undefined; pending: boolean; error: Error | null }
}) {
  const { report, pending, error } = validate
  return (
    <div
      style={{ display: 'flex', flexDirection: 'column', gap: 6, borderTop: '1px solid #eee', paddingTop: 10, marginTop: 4 }}
      data-testid="library-validation-result"
    >
      <span className="card-note">Last library validation</span>
      {pending && <span className="hint">Validating — staging and writing a few rows to a scratch table…</span>}
      {!pending && !report && !error && <span className="hint">Not validated yet.</span>}
      {!pending && error && <span className="hint" style={{ color: 'var(--danger)' }}>{error.message}</span>}
      {!pending && report && (
        <div className="row" style={{ gap: 8, alignItems: 'flex-start' }}>
          <span className="status">
            <span className={`dot ${report.succeeded ? 'dot-ok' : 'dot-bad'}`} />
            {report.succeeded ? 'validated' : 'failed'}
          </span>
          <span
            className="dim"
            style={{ lineHeight: 1.5, color: report.succeeded ? undefined : 'var(--danger)' }}
          >
            {report.output}
          </span>
        </div>
      )}
    </div>
  )
}

function CredentialSourceFields({ connectionName }: { connectionName: string | undefined }) {
  const { data } = useCredentialSource(connectionName)
  if (!data?.requiresCredential) return null

  return (
    <>
      <Field label="Credential store">
        {/* Disabled rather than absent: there is one store, so there is nothing to choose — but an
            operator hitting an auth failure needs to see which one is in play. */}
        <select className="select" value={data.store} disabled data-testid="credential-store-select">
          <option value={data.store}>{data.store}</option>
        </select>
      </Field>
      <Field label="Resolves as">
        <input
          className="input"
          readOnly
          value={data.environmentVariable}
          style={{ background: 'var(--sunken)', borderColor: '#f0eee8', color: 'var(--ink-3)' }}
          data-testid="credential-env-var"
        />
      </Field>
    </>
  )
}
