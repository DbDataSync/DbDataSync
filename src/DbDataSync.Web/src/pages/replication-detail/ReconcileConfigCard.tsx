import { Field } from '../../components/Field'
import type { AfterChangeStrategy, DeleteGuard, ReconcileConfig, ScheduleMode } from '../../api/types'

/**
 * Phase 125's automated delete reconciliation: a scheduled cadence, an after-change trigger, and the
 * guard a sweep runs with. Beside the Change Processing pipeline it complements — a watermark reader
 * never sees deletes, and this is what closes that gap without a full reload.
 *
 * The mode picker renders `every` inline (a smaller version of `ScheduleCard`'s own fields) rather than
 * reusing that component directly — `ScheduleCard` is written against `ReplicationTaskConfig.scheduling`
 * specifically, and threading a second, independent `SchedulingConfig | null` through it would be more
 * surface than the shared code saves.
 */
export function ReconcileConfigCard({ reconcile, onChange }: {
  reconcile: ReconcileConfig
  onChange: (next: ReconcileConfig) => void
}) {
  const every = reconcile.every

  const setEveryMode = (mode: ScheduleMode | 'off') =>
    onChange({
      ...reconcile,
      every: mode === 'off' ? null : mode === 'Continuous'
        ? { mode, frequencySeconds: every?.frequencySeconds ?? 3600, cronExpression: null }
        : { mode, frequencySeconds: null, cronExpression: every?.cronExpression ?? '0 * * * *' },
    })

  const setAfterChange = (strategy: AfterChangeStrategy) => onChange({ ...reconcile, afterChange: strategy })

  const setGuard = (guard: DeleteGuard) => onChange({ ...reconcile, deleteGuard: guard })

  const needsEvery = reconcile.afterChange.mode !== 'none' && every === null

  return (
    <div className={`card ${reconcile.enabled ? 'enabled' : 'disabled'}`} data-testid="reconcile-config-card">
      <div className="card-head">
        <span className="card-title">Delete reconciliation</span>
        <span className="card-note">sweeps for rows the reader's own incremental sync never sees deleted</span>
        <button
          type="button"
          className={`toggle ${reconcile.enabled ? 'on' : ''}`}
          onClick={() => onChange({ ...reconcile, enabled: !reconcile.enabled })}
          aria-pressed={reconcile.enabled}
          data-testid="reconcile-enabled-toggle"
        />
      </div>

      {reconcile.enabled && (
        <div className="card-body" style={{ gap: 12 }}>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16 }}>
            <Field label="Cadence">
              <div className="row" style={{ gap: 6 }}>
                <select
                  className="select sm"
                  value={every?.mode ?? 'off'}
                  onChange={(e) => setEveryMode(e.target.value as ScheduleMode | 'off')}
                  data-testid="reconcile-every-mode-select"
                >
                  <option value="off">Off — on-demand or after-change only</option>
                  <option value="Continuous">Every…</option>
                  <option value="Periodic">Periodic (cron)</option>
                </select>
                {every?.mode === 'Continuous' && (
                  <span className="row" style={{ gap: 4 }}>
                    <input
                      className="input sm mono"
                      style={{ width: 64 }}
                      type="number"
                      min={1}
                      value={every.frequencySeconds ?? 3600}
                      onChange={(e) => onChange({ ...reconcile, every: { ...every, frequencySeconds: Number(e.target.value) } })}
                      data-testid="reconcile-every-frequency-input"
                    />
                    <span className="hint">s</span>
                  </span>
                )}
                {every?.mode === 'Periodic' && (
                  <input
                    className="input sm mono"
                    style={{ width: 140 }}
                    value={every.cronExpression ?? ''}
                    onChange={(e) => onChange({ ...reconcile, every: { ...every, cronExpression: e.target.value } })}
                    placeholder="cron expression"
                    data-testid="reconcile-every-cron-input"
                  />
                )}
              </div>
            </Field>

            <Field label="After a change">
              <select
                className="select sm"
                value={reconcile.afterChange.mode}
                onChange={(e) => setAfterChange(e.target.value === 'any' ? { mode: 'any' } : { mode: 'none' })}
                data-testid="reconcile-after-change-select"
              >
                <option value="none">Never — cadence only</option>
                <option value="any">Any row read since the last sweep</option>
              </select>
            </Field>
          </div>

          {needsEvery && (
            <span className="hint" style={{ color: 'var(--warn)' }} data-testid="reconcile-needs-every-hint">
              An after-change trigger needs a cadence set above — it never fires more often than the
              cadence allows, so the cadence is also the floor on how often it can react to a change.
            </span>
          )}

          <Field label="Guard">
            <div className="row" style={{ gap: 6 }}>
              <select
                className="select sm"
                value={reconcile.deleteGuard.mode}
                onChange={(e) => setGuard(e.target.value === 'none' ? { mode: 'none' } : { mode: 'ratio', maxRatio: 0.5 })}
                data-testid="reconcile-guard-select"
              >
                <option value="ratio">Refuse over a ratio of the scope</option>
                <option value="none">No guard — always delete what's absent</option>
              </select>
              {reconcile.deleteGuard.mode === 'ratio' && (
                <span className="row" style={{ gap: 4 }}>
                  <input
                    className="input sm mono"
                    style={{ width: 56 }}
                    type="number"
                    min={0}
                    max={100}
                    value={Math.round(reconcile.deleteGuard.maxRatio * 100)}
                    onChange={(e) => setGuard({ mode: 'ratio', maxRatio: Number(e.target.value) / 100 })}
                    data-testid="reconcile-guard-ratio-input"
                  />
                  <span className="hint">% of the scope</span>
                </span>
              )}
            </div>
          </Field>

          <span className="hint">
            Always the <span className="mono">KeyReconcile</span>/<span className="mono">KeyReconcileDelete</span> pair —
            a segment is scoped from each table mapping's own default segmenting, the same as a Backfill.
          </span>
        </div>
      )}
    </div>
  )
}
