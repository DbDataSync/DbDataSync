import { useEffect, useState } from 'react'
import { Field } from './Field'

/**
 * The popup the Paused toggle opens — in **both** directions.
 *
 * There is no automatic rule about the note. Pausing with a reason, resuming with a note about how it
 * was resolved, and either with the note cleared are all things an operator legitimately wants, and
 * no default gets more than one of them right. So the control does not act: it asks, shows whatever
 * note is currently recorded, and commits only what is on screen when Confirm is pressed.
 *
 * Cancelling does nothing at all — no pause, no resume, no note written. A control that changes state
 * on the way to asking a question is worse than one that does not ask.
 */
export function PauseDialog({ paused, note, busy, onConfirm, onCancel }: {
  /** The state being moved *to* — true when this is a pause, false when it is a resume. */
  paused: boolean
  /** The note as it stands right now, which the operator may keep, change or clear. */
  note: string | null
  busy: boolean
  onConfirm: (note: string | null) => void
  onCancel: () => void
}) {
  const [draft, setDraft] = useState(note ?? '')

  // Escape cancels, because a popup that can only be dismissed by finding its button is a popup
  // people click Confirm on to get rid of.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onCancel() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onCancel])

  return (
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onCancel() }}>
      <div
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-label={paused ? 'Pause this replication' : 'Resume this replication'}
        data-testid="pause-dialog"
      >
        <div className="card-head">
          <span className="card-title">{paused ? 'Pause this replication' : 'Resume this replication'}</span>
        </div>
        <div className="card-body">
          <span className="hint">
            {paused
              ? 'Nothing new will be scheduled. A pass already running finishes normally, and the config is not touched.'
              : 'Scheduling resumes from the next tick, if the replication is also enabled.'}
          </span>

          <Field label="Note — recorded against this action">
            <textarea
              className="input"
              style={{ minHeight: 84, resize: 'vertical' }}
              value={draft}
              autoFocus
              placeholder={paused ? 'Why is this being held?' : 'Anything worth recording about resuming?'}
              onChange={(e) => setDraft(e.target.value)}
              data-testid="pause-note-input"
            />
          </Field>

          <div className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
            {/* Explicit, rather than making somebody select the text and delete it — clearing the
                note is one of the three things this popup exists to allow. */}
            <button
              type="button"
              className="btn btn-sm"
              onClick={() => setDraft('')}
              disabled={!draft}
              data-testid="pause-note-clear"
            >
              Clear note
            </button>
            <span className="spacer" />
            <button type="button" className="btn" onClick={onCancel} data-testid="pause-cancel">
              Cancel
            </button>
            <button
              type="button"
              className="btn btn-primary"
              disabled={busy}
              // An empty box is a cleared note, not an unwritten one, and the store keeps that
              // distinction — so it is sent as null rather than "".
              onClick={() => onConfirm(draft.trim() ? draft : null)}
              data-testid="pause-confirm"
            >
              {busy ? 'Saving…' : paused ? 'Pause' : 'Resume'}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
