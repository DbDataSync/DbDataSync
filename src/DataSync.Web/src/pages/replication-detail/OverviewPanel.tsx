import { useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Link } from 'react-router-dom'
import { Field } from '../../components/Field'
import { KeyValueTable } from '../../components/KeyValueTable'
import { ParameterForm } from '../../components/ParameterForm'
import { EndpointsCard } from './EndpointsCard'
import { ScriptBindingsCard } from '../../components/ScriptBindings'
import { readerNotes } from '../../api/readerNotes'
import { useConnections, useReplication, useReplicationCapabilities, useScripts, useTableMappings } from '../../api/hooks'
import type { ParameterDescriptor, ReplicationTaskConfig } from '../../api/types'

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
export function OverviewPanel({ replicationName, draft, setDraft }: {
  replicationName: string
  /** Owned by the layout route since phase 46, so an edit survives a look at another tab. */
  draft: ReplicationTaskConfig
  setDraft: (next: ReplicationTaskConfig) => void
}) {
  const { data: task, error } = useReplication(replicationName)
  const { data: mappings } = useTableMappings(replicationName)
  const { data: scripts } = useScripts()
  const mappingsBase = `/replications/${encodeURIComponent(replicationName)}/mappings`
  const capabilities = useReplicationCapabilities(replicationName)
  // These slots are source-side, so what a replication inherits is whatever its *source* connection
  // binds. Read from the saved task rather than the draft: changing the endpoint mid-edit should not
  // silently repoint what the INHERITED badge is describing.
  const { data: connections } = useConnections()
  const sourceConnection = connections?.find((c) => c.name === task?.endpoints?.source?.connectionName)

  const [stage, setStage] = useState<Stage>('reader')

  const setStageValue = (id: Stage, patch: object) =>
    setDraft({ ...draft, changeProcessing: { ...draft.changeProcessing, [id]: { ...draft.changeProcessing[id], ...patch } } })

  const kindsFor = (id: Stage) =>
    id === 'reader' ? capabilities.readers.map((r) => ({ kind: r.kind, note: readerNotes(r).join(' · ') || undefined }))
    : id === 'writer' ? capabilities.writers.map((w) => ({ kind: w.kind, note: w.supportsReconciliation ? 'reconciling' : 'upsert-only' }))
    : capabilities.stagingProviders.map((p) => ({ kind: p.kind, note: undefined }))

  /** What the chosen Kind says it takes. Declared by the component that reads them, so choosing a
   * Kind now offers its settings instead of leaving an operator to know the keys by heart. */
  const parametersFor = (id: Stage, kind: string): ParameterDescriptor[] =>
    (id === 'reader' ? capabilities.readers.find((r) => r.kind === kind)?.parameters
      : id === 'writer' ? capabilities.writers.find((w) => w.kind === kind)?.parameters
      : capabilities.stagingProviders.find((p) => p.kind === kind)?.parameters) ?? []

  const current = draft.changeProcessing[stage]
  const options = kindsFor(stage)
  const known = options.some((o) => o.kind === current.kind)
  const stageLabel = STAGES.find((s) => s.id === stage)!.label

  return (
    <div className="pane">
      <ErrorBanner error={error ?? capabilities.error} />

      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
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

              {/* The declared settings, plus whatever else is already in the bag. A Kind that
                  declares nothing still gets the free-form table, because an option a driver reads
                  but has not declared is still an option somebody set. */}
              <ParameterForm
                parameters={parametersFor(stage, current.kind)}
                values={current.options}
                onChange={(next) => setStageValue(stage, { options: next })}
                options={{ script: scripts?.map((s) => s.manifest.name) ?? [] }}
                testIdPrefix={`${stage}-options`}
              />
              {parametersFor(stage, current.kind).length === 0 && (
                <KeyValueTable
                  value={current.options}
                  onChange={(next) => setStageValue(stage, { options: next })}
                  addLabel="Setting name"
                  testId={`${stage}-options`}
                />
              )}

              <div className="row">
                {/* Save lives in the toolbar since phase 46: it commits endpoints, pipeline *and*
                    script bindings, and a button inside one of those three cards said otherwise. */}
                <span className="hint">Save settings, in the toolbar, commits to config history.</span>
                {/* The pipeline card says which reader, cache and writer will run; this is where to
                    find out what they will actually issue. Straight to the preview when there is only
                    one mapping to preview, and to the list when the answer depends on which. */}
                {mappings && mappings.length > 0 && (
                  <Link
                    className="btn-link spacer"
                    to={mappings.length === 1
                      ? `${mappingsBase}/${encodeURIComponent(mappings[0])}/preview`
                      : mappingsBase}
                    data-testid="preview-from-pipeline-link"
                  >
                    See the SQL this runs →
                  </Link>
                )}
              </div>
            </div>
          </div>

          <ScriptBindingsCard
            bindings={draft.scripts ?? {}}
            // What a mapping under this replication would inherit if the replication said nothing:
            // whatever the *source* connection binds, since these slots are source-side.
            inherited={sourceConnection?.scripts ?? {}}
            level="replication"
            onChange={(scripts) => setDraft({ ...draft, scripts })}
          />
      </div>
    </div>
  )
}
