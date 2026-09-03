import { useEffect } from 'react'
import { RunKindBadge, StatusBadge } from './StatusBadge'
import type { TaskRunRecord } from '../api/types'

/**
 * A failed run's full error, in a popup rather than the row's own inline expand.
 *
 * The run history table already has an expand-chevron for a *traced* run's stage timings — but a
 * failed run's error only ever showed as a hover tooltip on the status badge (easy to miss) or, for a
 * while, a wrapped block squeezed into the row's own grid columns. Neither is where somebody actually
 * goes looking for "why did this fail" — a popup is: it can be as wide as the message needs, it is
 * reachable the same way regardless of how the row happens to be laid out today, and it does not
 * compete with the row's own column widths the way an inline expansion does.
 */
export function RunErrorDialog({ run, onClose }: { run: TaskRunRecord; onClose: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose() }}>
      <div
        className="modal wide"
        role="dialog"
        aria-modal="true"
        aria-label={`Why '${run.mappingName}' failed`}
        data-testid="run-error-dialog"
      >
        <div className="card-head">
          <span className="card-title">Run failed</span>
          <span className="spacer row" style={{ gap: 8 }}>
            <RunKindBadge kind={run.runKind} />
            <StatusBadge status={run.status} />
          </span>
        </div>
        <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <div className="row" style={{ gap: 16, flexWrap: 'wrap' }}>
            <Field label="Mapping" value={run.mappingName} />
            {run.segmentLabel && <Field label="Segment" value={run.segmentLabel} />}
            {run.pid && <Field label="PID" value={String(run.pid)} mono />}
            {run.startedAtUtc && <Field label="Started" value={new Date(run.startedAtUtc).toLocaleString()} />}
            {run.endedAtUtc && <Field label="Ended" value={new Date(run.endedAtUtc).toLocaleString()} />}
          </div>

          {run.failureKind && (
            <span className="hint">
              {run.failureKind === 'PositionExpired'
                ? 'The source no longer holds the changes this pass needed — the fix is a resync, from the run list, not a retry.'
                : `Failure kind: ${run.failureKind}`}
            </span>
          )}

          <div>
            <div className="hint" style={{ marginBottom: 4 }}>Error</div>
            <div className="run-error-detail modal-error" data-testid="run-error-dialog-message">
              {run.errorSummary ?? 'No error message was recorded for this run.'}
            </div>
          </div>

          <div className="row" style={{ justifyContent: 'flex-end' }}>
            <button type="button" className="btn" onClick={onClose} data-testid="run-error-dialog-close">
              Close
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}

function Field({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <span>
      <div className="hint">{label}</div>
      <div className={mono ? 'mono' : undefined}>{value}</div>
    </span>
  )
}
