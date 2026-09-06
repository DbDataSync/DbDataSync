import type { ReplicationTaskConfig, ScheduleMode } from '../../api/types'

/**
 * When this replication runs — on Overview, between the endpoints and the sub-tabs, since phase 103.
 *
 * **A single line, not the full-height card this used to be.** It lived in the detail rail until
 * then, where a whole card's worth of vertical room was free; on Overview it sits above tabbed
 * content that wants that room back, so the fields that used to stack in a `card-body` sit in the
 * `card-head` instead, beside the title, the way `MetricsCard`'s window selector already does.
 *
 * **What a complex schedule does here is a judgement call, not a rule.** A cron expression is
 * typically short enough to sit on one line beside the mode picker; if a particular one ever is not,
 * the input scrolls its own overflow rather than widening the row — nothing here truncates a value
 * that was actually typed in.
 *
 * **The Enabled toggle is deliberately not here.** It lives in the replication's header and commits on
 * its own, while these fields belong to the batched Save. Leaving Enabled in this card — even in a
 * corner — would say the two save the same way, and they do not: one takes effect the moment it is
 * clicked, the others wait for Save. Putting them in different places is what makes that visible
 * rather than something an operator discovers.
 *
 * The accent still reflects enabled/disabled, because the card is about *this replication's* running,
 * and that is true whether or not the control that drives it lives here.
 *
 * **Concurrency sits here too, not on the Pipeline tab.** How many table mappings the worker processes
 * at once is a fact about *how this replication runs*, the same kind of fact as how often it runs and
 * when its worker gives up — not about which reader/staging/writer the pipeline uses. Like the other
 * fields on this card (and unlike the Enabled toggle), it belongs to the batched Save.
 */
export function ScheduleCard({ draft, enabled, onChange }: {
  draft: ReplicationTaskConfig
  /** The **saved** state, not the draft's — this commits on its own, so the draft never holds it. */
  enabled: boolean
  onChange: (next: ReplicationTaskConfig) => void
}) {
  const setScheduling = (patch: Partial<ReplicationTaskConfig['scheduling']>) =>
    onChange({ ...draft, scheduling: { ...draft.scheduling, ...patch } })

  const setDegreeOfParallelism = (value: number) =>
    onChange({ ...draft, changeProcessing: { ...draft.changeProcessing, degreeOfParallelism: value } })

  const mode = draft.scheduling.mode

  return (
    <div className={`card ${enabled ? 'enabled' : 'disabled'}`} data-testid="replication-schedule-card">
      <div className="card-head" style={{ flexWrap: 'wrap', rowGap: 8 }}>
        <span className="card-title">Schedule</span>

        <select
          className="select sm"
          style={{ width: 'auto', flex: 'none' }}
          value={mode}
          onChange={(e) => setScheduling({ mode: e.target.value as ScheduleMode })}
          data-testid="schedule-mode-select"
        >
          <option value="Continuous">Continuous</option>
          <option value="Periodic">Periodic (cron)</option>
        </select>

        {mode === 'Continuous' ? (
          <span className="row" style={{ gap: 6, flex: 'none' }}>
            <span className="hint">every</span>
            <input
              className="input sm mono"
              style={{ width: 56 }}
              type="number"
              min={1}
              value={draft.scheduling.frequencySeconds ?? 60}
              onChange={(e) => setScheduling({ frequencySeconds: Number(e.target.value) })}
              data-testid="schedule-frequency-input"
            />
            <span className="hint">s · idle timeout</span>
            <input
              className="input sm mono"
              style={{ width: 56 }}
              type="number"
              min={1}
              // Blank rather than 60 when unset, because "nobody said" and "somebody chose 60" are
              // different answers and only one of them gets written down.
              placeholder="60"
              value={draft.scheduling.idleTimeoutSeconds ?? ''}
              onChange={(e) => setScheduling({
                idleTimeoutSeconds: e.target.value ? Number(e.target.value) : null,
              })}
              data-testid="schedule-idle-timeout-input"
            />
            <span className="hint">s</span>
          </span>
        ) : (
          <input
            className="input sm mono"
            style={{ width: 200, flex: 'none' }}
            value={draft.scheduling.cronExpression ?? ''}
            onChange={(e) => setScheduling({ cronExpression: e.target.value })}
            placeholder="cron expression"
            data-testid="schedule-cron-input"
          />
        )}

        <span className="row" style={{ gap: 6, flex: 'none' }}>
          <span className="hint">· up to</span>
          <input
            className="input sm mono"
            style={{ width: 44 }}
            type="number"
            min={1}
            value={draft.changeProcessing.degreeOfParallelism ?? 4}
            onChange={(e) => setDegreeOfParallelism(Number(e.target.value) || 1)}
            data-testid="schedule-degree-of-parallelism-input"
          />
          <span className="hint">mapping(s) at once</span>
        </span>

        <span
          className="hint spacer"
          style={{ textAlign: 'right' }}
          title={
            mode === 'Continuous'
              ? 'Continuous mode re-reads changes on every interval. The worker stays running between ' +
                'intervals and exits once it has gone the idle timeout without finding a single changed row.'
              : 'Periodic mode runs on the cron expression above.'
          }
        >
          {mode === 'Continuous' ? 'worker exits idle, restarts on schedule' : 'runs on the cron expression'}
        </span>
      </div>
    </div>
  )
}
