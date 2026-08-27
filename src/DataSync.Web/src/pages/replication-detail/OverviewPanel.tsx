import { useEffect, useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Field } from '../../components/Field'
import { KeyValueTable } from '../../components/KeyValueTable'
import { EndpointsCard } from './EndpointsCard'
import { useReplication, useReplicationCapabilities, useTableMappings, useUpsertReplication } from '../../api/hooks'
import type { ReplicationTaskConfig, ScheduleMode } from '../../api/types'

type Stage = 'reader' | 'cache' | 'writer'

const STAGES: { id: Stage; label: string }[] = [
  { id: 'reader', label: 'Reader' },
  { id: 'cache', label: 'Staging' },
  { id: 'writer', label: 'Writer' },
]

/**
 * The design turns the pipeline into three selectable stages — Reader → Staging → Writer — with the
 * selected stage's implementation and its settings below. That is a better fit for the data than the
 * three stacked Kind pickers plus raw-JSON textareas it replaces: a stage's Kind and its options
 * belong together, and the options are a string dictionary, which a two-column table states plainly.
 */
export function OverviewPanel({ replicationName }: { replicationName: string }) {
  const { data: task, error } = useReplication(replicationName)
  const { data: mappings } = useTableMappings(replicationName)
  const capabilities = useReplicationCapabilities(replicationName)
  const upsert = useUpsertReplication()

  const [draft, setDraft] = useState<ReplicationTaskConfig | null>(null)
  const [stage, setStage] = useState<Stage>('reader')

  useEffect(() => {
    if (task && !draft) setDraft(task)
  }, [task, draft])

  if (!draft) {
    return (
      <div className="pane">
        <ErrorBanner error={error} />
        <span className="hint">Loading…</span>
      </div>
    )
  }

  const setStageValue = (id: Stage, patch: object) =>
    setDraft({ ...draft, changeProcessing: { ...draft.changeProcessing, [id]: { ...draft.changeProcessing[id], ...patch } } })

  const kindsFor = (id: Stage) =>
    id === 'reader' ? capabilities.readers.map((r) => ({ kind: r.kind, note: r.supportsSegmentation ? 'segmentable' : undefined }))
    : id === 'writer' ? capabilities.writers.map((w) => ({ kind: w.kind, note: w.supportsReconciliation ? 'reconciling' : 'upsert-only' }))
    : capabilities.stagingProviders.map((p) => ({ kind: p.kind, note: undefined }))

  const current = draft.changeProcessing[stage]
  const options = kindsFor(stage)
  const known = options.some((o) => o.kind === current.kind)
  const stageLabel = STAGES.find((s) => s.id === stage)!.label

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    await upsert.mutateAsync({ name: replicationName, task: draft })
  }

  return (
    <div className="pane">
      <ErrorBanner error={upsert.error ?? capabilities.error} />

      <form onSubmit={save} style={{ display: 'flex', gap: 18, alignItems: 'flex-start' }}>
        <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 14 }}>
          <EndpointsCard
            endpoints={draft.endpoints ?? { source: null, target: null }}
            mappingCount={mappings?.length}
            onChange={(endpoints) => setDraft({ ...draft, endpoints })}
          />

          <div className="card">
            <div className="card-head" style={{ alignItems: 'flex-start', paddingTop: 11 }}>
              <span className="card-title" style={{ width: 64, flex: 'none', paddingTop: 10 }}>Pipeline</span>
              <div className="stages">
                {STAGES.map((s, i) => (
                  <span key={s.id} style={{ display: 'contents' }}>
                    {i > 0 && <span className="arrow">→</span>}
                    <button
                      type="button"
                      className={`stage ${stage === s.id ? 'active' : ''}`}
                      onClick={() => setStage(s.id)}
                      data-testid={`stage-${s.id}`}
                    >
                      <span className="stage-name">{s.label}</span>
                      <span className="stage-impl">{draft.changeProcessing[s.id].kind}</span>
                    </button>
                  </span>
                ))}
              </div>
              <span style={{ width: 64, flex: 'none' }} />
            </div>

            <div className="card-body" style={{ padding: 14, gap: 12 }}>
              <div style={{ display: 'grid', gridTemplateColumns: '1.5fr 1fr', gap: 16 }}>
                <Field label={`${stageLabel} implementation`}>
                  <select
                    className="select"
                    value={current.kind}
                    onChange={(e) => setStageValue(stage, { kind: e.target.value })}
                    data-testid={`${stage}-kind-select`}
                  >
                    {!known && current.kind && <option value={current.kind}>{current.kind} — not offered by this driver</option>}
                    {options.map((o) => (
                      <option key={o.kind} value={o.kind}>{o.note ? `${o.kind} — ${o.note}` : o.kind}</option>
                    ))}
                  </select>
                </Field>
                <Field label="Applies to">
                  <span className="input" style={{ display: 'flex', alignItems: 'center', background: 'var(--sunken)', borderColor: 'var(--chrome-edge)', color: 'var(--ink-4)', fontFamily: 'var(--ui)', fontSize: 12 }}>
                    {!mappings ? '…'
                      : mappings.length === 1 ? 'The 1 table mapping'
                      : `All ${mappings.length} table mappings`}
                  </span>
                </Field>
              </div>

              <KeyValueTable
                value={current.options}
                onChange={(next) => setStageValue(stage, { options: next })}
                addLabel="Setting name"
                testId={`${stage}-options`}
              />

              <div className="row">
                <button type="submit" className="btn btn-primary" disabled={upsert.isPending} data-testid="save-settings-button">
                  {upsert.isPending ? 'Saving…' : 'Save settings'}
                </button>
                <span className="hint">Saving commits to config history.</span>
              </div>
            </div>
          </div>
        </div>

        <div style={{ width: 288, flex: 'none', display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div className="card">
            <div className="card-head"><span className="card-title">Schedule</span></div>
            <div className="card-body">
              <div className="row">
                <button
                  type="button"
                  className={`toggle ${draft.enabled ? 'on' : ''}`}
                  onClick={() => setDraft({ ...draft, enabled: !draft.enabled })}
                  aria-pressed={draft.enabled}
                  data-testid="enabled-toggle"
                />
                <span style={{ font: '500 12px var(--ui)', color: 'var(--ink)' }}>
                  {draft.enabled ? 'Enabled' : 'Disabled'}
                </span>
              </div>
              <div className="divider" />
              <Field label="Schedule mode">
                <select
                  className="select"
                  value={draft.scheduling.mode}
                  onChange={(e) => setDraft({ ...draft, scheduling: { ...draft.scheduling, mode: e.target.value as ScheduleMode } })}
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
                    onChange={(e) => setDraft({ ...draft, scheduling: { ...draft.scheduling, frequencySeconds: Number(e.target.value) } })}
                  />
                </Field>
              ) : (
                <Field label="Cron expression">
                  <input
                    className="input"
                    value={draft.scheduling.cronExpression ?? ''}
                    onChange={(e) => setDraft({ ...draft, scheduling: { ...draft.scheduling, cronExpression: e.target.value } })}
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
        </div>
      </form>
    </div>
  )
}
