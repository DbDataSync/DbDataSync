import { Field } from '../../components/Field'
import type { ReplicationTaskConfig, ScheduleMode } from '../../api/types'

/**
 * When this replication runs.
 *
 * **The Enabled toggle is deliberately not here.** It lives in the replication's header and commits on
 * its own, while these fields belong to the batched Save. Leaving Enabled in this card — even in a
 * corner — would say the two save the same way, and they do not: one takes effect the moment it is
 * clicked, the others wait for Save. Putting them in different places is what makes that visible
 * rather than something an operator discovers.
 *
 * The accent still reflects enabled/disabled, because the card is about *this replication's* running,
 * and that is true whether or not the control that drives it lives here.
 */
export function ScheduleCard({ draft, enabled, onChange }: {
  draft: ReplicationTaskConfig
  /** The **saved** state, not the draft's — this commits on its own, so the draft never holds it. */
  enabled: boolean
  onChange: (next: ReplicationTaskConfig) => void
}) {
  const setScheduling = (patch: Partial<ReplicationTaskConfig['scheduling']>) =>
    onChange({ ...draft, scheduling: { ...draft.scheduling, ...patch } })

  return (
    <div className={`card ${enabled ? 'enabled' : 'disabled'}`} data-testid="replication-schedule-card">
      <div className="card-head"><span className="card-title">Replication schedule</span></div>
      <div className="card-body">
        <Field label="Schedule mode">
          <select
            className="select"
            value={draft.scheduling.mode}
            onChange={(e) => setScheduling({ mode: e.target.value as ScheduleMode })}
            data-testid="schedule-mode-select"
          >
            <option value="Continuous">Continuous</option>
            <option value="Periodic">Periodic (cron)</option>
          </select>
        </Field>

        {draft.scheduling.mode === 'Continuous' ? (
          <Field label="Frequency (seconds)">
            <input
              className="input"
              type="number"
              min={1}
              value={draft.scheduling.frequencySeconds ?? 60}
              onChange={(e) => setScheduling({ frequencySeconds: Number(e.target.value) })}
              data-testid="schedule-frequency-input"
            />
          </Field>
        ) : (
          <Field label="Cron expression">
            <input
              className="input"
              value={draft.scheduling.cronExpression ?? ''}
              onChange={(e) => setScheduling({ cronExpression: e.target.value })}
              data-testid="schedule-cron-input"
            />
          </Field>
        )}

        <span className="hint">
          {draft.scheduling.mode === 'Continuous'
            ? 'Continuous mode re-reads changes on every interval.'
            : 'Periodic mode runs on the cron expression above.'}
        </span>
      </div>
    </div>
  )
}
