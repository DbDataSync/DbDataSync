import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { INTENT_INFO } from '../pages/replication-detail/readIntent'
import type { ReadHold, ReadIntent } from '../api/types'

/**
 * The popup a mapping row's "Manage" button opens — where the row is a summary and this is where you
 * act, the same relationship `RunDetailsDialog` already has to its row (phase 102's own open question,
 * resolved the same way).
 *
 * Two different shapes depending on why it was opened, because they are different questions:
 *
 * - **Recovering from `PositionExpired`** is a choice between two named options, not a dropdown — see
 *   `RecoverView`. Today's only path out of this hold is a full `Resync` from Run History; this adds
 *   the cheaper alternative and frames both honestly rather than defaulting to the expensive one.
 * - **Changing the intent normally** is a picker, filtered to what this mapping's reader can honestly
 *   do (`offered`, always including `InitialLoad`). Picking `ChangesFromLatest` — deliberate data loss
 *   — does not commit from the picker; it opens `DataLossConfirm`, which names what is being skipped
 *   rather than asking a bare "are you sure".
 */
export function MappingReadStateDialog({
  mappingName, sourceLabel, currentIntent, currentHold, offered,
  hasVerificationChecks, verificationHref, verificationPending, onRunVerification,
  busy, error, onConfirm, onCancel,
}: {
  mappingName: string
  /** What a `ChangesFromLatest` confirmation names as what would be skipped. */
  sourceLabel: string
  currentIntent: ReadIntent
  currentHold: ReadHold
  /** Always includes `InitialLoad` — see `readIntent.ts`'s `offeredIntents`. */
  offered: ReadIntent[]
  hasVerificationChecks: boolean
  verificationHref: string
  verificationPending: boolean
  onRunVerification: () => void
  busy: boolean
  error?: unknown
  onConfirm: (next: { intent: ReadIntent; hold: ReadHold }) => void
  onCancel: () => void
}) {
  const recovering = currentHold === 'PositionExpired'
  const [draft, setDraft] = useState<ReadIntent>(currentIntent)
  // Non-null only while the ChangesFromLatest confirmation is open — a second, explicit step, because
  // that intent is deliberate data loss and a bare "are you sure" does not say what is being lost.
  const [confirmingDataLoss, setConfirmingDataLoss] = useState(false)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onCancel() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onCancel])

  const continueOrConfirm = () => {
    if (draft === 'ChangesFromLatest' && !confirmingDataLoss) {
      setConfirmingDataLoss(true)
      return
    }
    // Preserves whatever hold is already on this row (usually None; Paused if an operator is changing
    // the intent of a mapping they have paused without also resuming it) — this dialog only ever
    // changes the intent outside of the recovery path.
    onConfirm({ intent: draft, hold: currentHold })
  }

  return (
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onCancel() }}>
      <div
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-label={`Read intent for '${mappingName}'`}
        data-testid="read-state-dialog"
      >
        <div className="card-head">
          <span className="card-title mono">{mappingName}</span>
        </div>
        <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          {error != null && (
            <div className="banner error" role="alert">
              {error instanceof Error ? error.message : String(error)}
            </div>
          )}

          {recovering ? (
            <RecoverView
              offered={offered}
              busy={busy}
              onChoose={(intent) => onConfirm({ intent, hold: 'None' })}
              onCancel={onCancel}
            />
          ) : confirmingDataLoss ? (
            <DataLossConfirm
              sourceLabel={sourceLabel}
              hasVerificationChecks={hasVerificationChecks}
              verificationHref={verificationHref}
              verificationPending={verificationPending}
              onRunVerification={onRunVerification}
              busy={busy}
              onBack={() => setConfirmingDataLoss(false)}
              onConfirm={() => onConfirm({ intent: 'ChangesFromLatest', hold: currentHold })}
            />
          ) : (
            <>
              <span className="hint">
                What this mapping's next pass does. Takes effect for the next scheduled pass, not one
                already running.
              </span>

              <select
                className="select"
                value={draft}
                onChange={(e) => setDraft(e.target.value as ReadIntent)}
                data-testid="read-state-intent-select"
              >
                {offered.map((intent) => (
                  <option key={intent} value={intent}>{INTENT_INFO[intent].label}</option>
                ))}
              </select>
              <span className="hint">{INTENT_INFO[draft].hint}</span>

              <div className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
                <button type="button" className="btn" onClick={onCancel} data-testid="read-state-cancel">
                  Cancel
                </button>
                <button
                  type="button"
                  className={`btn ${draft === 'ChangesFromLatest' ? 'btn-danger' : 'btn-primary'}`}
                  disabled={busy || draft === currentIntent}
                  onClick={continueOrConfirm}
                  data-testid="read-state-confirm"
                >
                  {busy ? 'Saving…' : draft === 'ChangesFromLatest' ? 'Continue…' : 'Set intent'}
                </button>
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  )
}

/**
 * `PositionExpired`'s recovery, presented as the choice it is — see phase 102's own doc, which is
 * blunt about the alternative: "today the operator gets Resync and no alternative, which on a large
 * table is hours."
 */
function RecoverView({ offered, busy, onChoose, onCancel }: {
  offered: ReadIntent[]
  busy: boolean
  onChoose: (intent: ReadIntent) => void
  onCancel: () => void
}) {
  const canFromEarliest = offered.includes('ChangesFromEarliest')

  return (
    <>
      <div className="banner warn" role="alert">
        The source discarded history a pass of this mapping needed, so it is held until somebody
        chooses how to recover. Nothing runs until one of these is picked.
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {canFromEarliest && (
          <RecoveryOption
            title={INTENT_INFO.ChangesFromEarliest.label}
            description="Catches up from the oldest position the source can still answer for — usually minutes, and loses only what the source itself already discarded."
            busy={busy}
            onClick={() => onChoose('ChangesFromEarliest')}
            testId="read-state-recover-earliest"
          />
        )}
        <RecoveryOption
          title={INTENT_INFO.InitialLoad.label}
          description="The full reload, for when catching up from the earliest surviving position is not enough. Correct, and can take hours on a large table."
          busy={busy}
          onClick={() => onChoose('InitialLoad')}
          testId="read-state-recover-initial-load"
        />
      </div>

      <div className="row" style={{ justifyContent: 'flex-end' }}>
        <button type="button" className="btn" onClick={onCancel} data-testid="read-state-cancel">
          Cancel
        </button>
      </div>
    </>
  )
}

function RecoveryOption({ title, description, busy, onClick, testId }: {
  title: string
  description: string
  busy: boolean
  onClick: () => void
  testId: string
}) {
  return (
    <button
      type="button"
      className="btn"
      style={{
        textAlign: 'left', display: 'flex', flexDirection: 'column', alignItems: 'flex-start',
        gap: 4, padding: '10px 12px', height: 'auto',
      }}
      disabled={busy}
      onClick={onClick}
      data-testid={testId}
    >
      <strong>{title}</strong>
      <span className="hint">{description}</span>
    </button>
  )
}

/**
 * `ChangesFromLatest`'s confirmation — named data loss, not a generic "are you sure", per phase 102's
 * requirement. Offers a verification check beside it when this mapping has any configured (phase 43),
 * rather than inventing a new way to answer "is this table already in sync".
 */
function DataLossConfirm({
  sourceLabel, hasVerificationChecks, verificationHref, verificationPending, onRunVerification,
  busy, onBack, onConfirm,
}: {
  sourceLabel: string
  hasVerificationChecks: boolean
  verificationHref: string
  verificationPending: boolean
  onRunVerification: () => void
  busy: boolean
  onBack: () => void
  onConfirm: () => void
}) {
  return (
    <>
      <div className="banner error" role="alert" data-testid="read-state-data-loss-warning">
        {/* One child, deliberately: `.banner` is `display: flex`, so several inline nodes directly
            inside it — text runs, `<strong>`, the `<span className="mono">` — each become their own
            flex item and lay out as narrow side-by-side columns, each wrapping independently, rather
            than as one paragraph. Wrapping the whole sentence in a single span keeps it the one flex
            item the layout expects, matching `ErrorBanner`'s icon-plus-one-content-span shape. */}
        <span>
          <strong>This skips data.</strong> Every change to <span className="mono">{sourceLabel}</span>{' '}
          made before this mapping's next pass runs will never be replicated — this adopts the
          source's current position without reading anything that came before it.
        </span>
      </div>

      {hasVerificationChecks ? (
        <span className="hint">
          Before deciding, a verification check can say whether the two sides already agree —{' '}
          <button
            type="button"
            className="btn-link"
            onClick={onRunVerification}
            disabled={verificationPending}
            data-testid="read-state-run-verification"
          >
            {verificationPending ? 'Queueing…' : 'run checks now'}
          </button>{' '}
          (results appear on this mapping's <Link to={verificationHref}>Verify tab</Link>).
        </span>
      ) : (
        <span className="hint">
          This mapping has no verification checks configured, so there is no automatic way to confirm
          the two sides already agree before doing this.
        </span>
      )}

      <div className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
        <button type="button" className="btn" onClick={onBack} data-testid="read-state-back">
          Back
        </button>
        <button
          type="button"
          className="btn btn-danger"
          disabled={busy}
          onClick={onConfirm}
          data-testid="read-state-confirm-data-loss"
        >
          {busy ? 'Saving…' : 'Skip to now'}
        </button>
      </div>
    </>
  )
}
