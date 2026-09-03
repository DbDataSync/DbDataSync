import { useEffect } from 'react'
import { RunKindBadge, StatusBadge } from './StatusBadge'
import { TimingDetail, WatermarkCell } from './RunFigures'
import { processingTime, queueTime } from './runTimes'
import type { RunWatermarkTimes, TaskRunRecord } from '../api/types'

/**
 * One run, in full, in a popup — opened from its status in the history table.
 *
 * It began as a failed run's error and nothing else: that error only ever showed as a hover tooltip
 * on the status badge (easy to miss) or, for a while, a wrapped block squeezed into the row's own
 * grid columns. Neither is where somebody goes looking for "why did this fail" — a popup is: it can
 * be as wide as the message needs, it is reachable the same way regardless of how the row is laid out
 * today, and it does not compete with the row's column widths the way an inline expansion does.
 *
 * **Every run opens it now, not only a failed one** (phase 96). "How long did this take, how many
 * rows, where did the watermark land" is an ordinary question about a run that *worked*, and the row
 * answers it only in truncated cells and a tooltip. The error block is what is conditional; nothing
 * else about the dialog depends on how the run ended.
 *
 * One adaptive dialog rather than two components, because the fields are the same fields and a failed
 * run wants every one of them *plus* its error. Two would mean two places to add the next field to,
 * and they would drift. Splitting them is easy if they ever genuinely diverge.
 */
export function RunDetailsDialog({ run, times, onClose }: {
  run: TaskRunRecord
  /** When the watermark's positions resolved to source-side times. Undefined is a normal answer. */
  times: RunWatermarkTimes | undefined
  onClose: () => void
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const failed = run.status === 'Failed'
  const queued = queueTime(run)

  return (
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose() }}>
      <div
        className="modal wide"
        role="dialog"
        aria-modal="true"
        aria-label={`Run of '${run.mappingName}'`}
        data-testid="run-details-dialog"
      >
        <div className="card-head">
          <span className="card-title">{failed ? 'Run failed' : 'Run details'}</span>
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

          {/* The counts and the two times the row can only show truncated, or — in queue time's case —
              only as a tooltip on another cell. */}
          <div className="row" style={{ gap: 16, flexWrap: 'wrap' }} data-testid="run-details-figures">
            <Field label="Rows read" value={run.rowsRead.toLocaleString()} />
            <Field label="Rows written" value={run.rowsWritten.toLocaleString()} />
            <Field label="Processing time" value={processingTime(run)} />
            {/* Null when the wait rounded to nothing, which is the ordinary case and not worth a
                field saying so. queueTime words itself as a sentence for its tooltip use, so the
                figure here is the sentence — it is the only place queue time is ever stated. */}
            {queued && <Field label="Queue time" value={queued} />}
          </div>

          <div>
            <div className="hint" style={{ marginBottom: 4 }}>Watermark</div>
            {/* The same cell the history row renders, so the two cannot disagree about what this
                run's position did. It says its own "—" when the run moved no watermark at all — a
                backfill, a verification, or a pass that failed before it made one durable. */}
            <WatermarkCell run={run} times={times} />
          </div>

          {run.failureKind && (
            <span className="hint">
              {run.failureKind === 'PositionExpired'
                ? 'The source no longer holds the changes this pass needed — the fix is a resync, from the run list, not a retry.'
                : `Failure kind: ${run.failureKind}`}
            </span>
          )}

          {/* Only a traced pass has one. Here as well as under the row's own chevron, because
              somebody who opened this to ask how long the run took is already asking the question
              the trace breaks down. */}
          {run.timing && (
            <div>
              <div className="hint" style={{ marginBottom: 4 }}>Stage timings</div>
              <TimingDetail timing={run.timing} runId={run.runId} />
            </div>
          )}

          {/* Conditional, and the only conditional part: a run that succeeded has no error, and an
              "Error: none" block would be a dialog reporting the absence of a problem. */}
          {failed && (
            <div>
              <div className="hint" style={{ marginBottom: 4 }}>Error</div>
              <div className="run-error-detail modal-error" data-testid="run-details-dialog-message">
                {run.errorDetail ?? run.errorSummary ?? 'No error message was recorded for this run.'}
              </div>
            </div>
          )}

          <div className="row" style={{ justifyContent: 'flex-end' }}>
            <button type="button" className="btn" onClick={onClose} data-testid="run-details-dialog-close">
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
