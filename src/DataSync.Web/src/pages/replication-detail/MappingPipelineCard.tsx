import { useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Field } from '../../components/Field'
import { KeyValueTable } from '../../components/KeyValueTable'
import { ParameterForm } from '../../components/ParameterForm'
import { readerNotes } from '../../api/readerNotes'
import { useCapabilities, useInferredNaturalKey, useScripts } from '../../api/hooks'
import type {
  CacheConfig, ParameterDescriptor, ReaderConfig, ReplicationTaskConfig, ResolvedRef, WriterConfig,
} from '../../api/types'
import { NATURAL_KEY, versionsRows, withoutNaturalKey } from './naturalKey'

/** The three stages a mapping can answer for itself, and what each is called on screen. */
type Stage = 'reader' | 'cache' | 'writer'

const STAGES: { id: Stage; label: string }[] = [
  { id: 'reader', label: 'Reader' },
  { id: 'cache', label: 'Staging' },
  { id: 'writer', label: 'Writer' },
]

/** The three overrides, as the mapping stores them. Null on any one of them means inherit. */
export interface PipelineOverrides {
  readerOverride: ReaderConfig | null
  cacheOverride: CacheConfig | null
  writerOverride: WriterConfig | null
}

const FIELD_OF: Record<Stage, keyof PipelineOverrides> = {
  reader: 'readerOverride',
  cache: 'cacheOverride',
  writer: 'writerOverride',
}

/**
 * This mapping's pipeline: the replication's, unless this table needs something else.
 *
 * Structurally the Overview's own Pipeline tab — the same three-stage Reader → Staging → Writer
 * picker over the same `ParameterForm` — with an inherit/override toggle on each stage. Overriding
 * starts from what the replication currently has rather than from a blank form: an override is nearly
 * always "the same, except for one thing", and making somebody re-pick a Kind they did not want to
 * change would be the form arguing with them.
 *
 * **An override replaces its stage whole**, Kind and options together, which is why the toggle is per
 * stage rather than per field. Half a stage inherited would mean an option set for one Kind quietly
 * surviving onto another.
 */
export function MappingPipelineCard({
  replicationName, mappingName, task, source, target, overrides, onChange,
}: {
  replicationName: string
  /** The name as *saved*. The derived-key preview reads config off disk, so a mapping being renamed
   * in this form does not exist under its new name yet — same rule the column editor follows. */
  mappingName: string | undefined
  task: ReplicationTaskConfig | undefined
  source: ResolvedRef
  target: ResolvedRef
  overrides: PipelineOverrides
  onChange: (next: PipelineOverrides) => void
}) {
  const [stage, setStage] = useState<Stage>('writer')
  const { data: scripts } = useScripts()

  // Each side's own driver, not one connection's for all three: a reader Kind is the source's
  // question and staging and the writer are the target's, which is how the server resolves them and
  // how it validates an override on save.
  const sourceCapabilities = useCapabilities(source.connectionName || undefined)
  const targetCapabilities = useCapabilities(target.connectionName || undefined)

  const inherited = task?.changeProcessing
  const override = overrides[FIELD_OF[stage]]
  const effective = override ?? (inherited ? inherited[stage] : undefined)
  const overriding = override !== null

  const setStageValue = (patch: object) =>
    onChange({ ...overrides, [FIELD_OF[stage]]: { ...effective, ...patch } })

  const toggleOverride = () =>
    onChange({
      ...overrides,
      // Off drops back to inheriting; on starts from whatever is currently in effect, so it is a
      // starting point rather than a reset. The same gesture `InheritableToggle` makes for a boolean.
      [FIELD_OF[stage]]: overriding ? null : structuredClone(effective ?? { kind: '', options: {} }),
    })

  const kindsFor = (id: Stage) =>
    id === 'reader'
      ? (sourceCapabilities.data?.readers ?? []).map((r) => ({ kind: r.kind, note: readerNotes(r).join(' · ') || undefined }))
      : id === 'writer'
        ? (targetCapabilities.data?.writers ?? []).map((w) => ({ kind: w.kind, note: w.supportsReconciliation ? 'reconciling' : 'upsert-only' }))
        : (targetCapabilities.data?.stagingProviders ?? []).map((p) => ({ kind: p.kind, note: undefined }))

  const parametersFor = (id: Stage, kind: string): ParameterDescriptor[] =>
    (id === 'reader' ? sourceCapabilities.data?.readers.find((r) => r.kind === kind)?.parameters
      : id === 'writer' ? targetCapabilities.data?.writers.find((w) => w.kind === kind)?.parameters
      : targetCapabilities.data?.stagingProviders.find((p) => p.kind === kind)?.parameters) ?? []

  /** What each stage actually runs, for the strip along the top — this mapping's, or the one it inherits. */
  const kindOf = (id: Stage) =>
    (overrides[FIELD_OF[id]] ?? (inherited ? inherited[id] : undefined))?.kind || '…'

  // Presence, not emptiness: an override toggled on and not yet typed into is still an override
  // somebody is in the middle of making, and collapsing it to "inherit" would undo the click.
  const stated = overrides.writerOverride?.options !== undefined && NATURAL_KEY in overrides.writerOverride.options

  /**
   * Stating a natural key *is* overriding the writer — there is nowhere else for the value to live —
   * so this seeds the override from the replication's writer and edits one key in it. Null takes the
   * key back out, which is what makes the stage derive again.
   */
  const setNaturalKey = (next: string | null) => {
    const base = overrides.writerOverride ?? structuredClone(inherited!.writer)
    const options = { ...base.options }
    if (next === null) delete options[NATURAL_KEY]
    else options[NATURAL_KEY] = next

    onChange({ ...overrides, writerOverride: { ...base, options } })
  }

  const kinds = kindsFor(stage)
  const kind = effective?.kind ?? ''
  const known = kinds.some((o) => o.kind === kind)
  const declared = parametersFor(stage, kind)
  const stageLabel = STAGES.find((s) => s.id === stage)!.label

  // The natural key gets its own control below, so it is taken out of the generic form here.
  const historizing = stage === 'writer' && versionsRows(kind)
  const editable = historizing ? withoutNaturalKey(declared) : declared

  return (
    <>
      <ErrorBanner error={sourceCapabilities.error ?? targetCapabilities.error} />
      <div className="card" data-testid="mapping-pipeline">
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
                  data-testid={`mapping-stage-${s.id}`}
                >
                  <span className="stage-name">{s.label}</span>
                  <span className="stage-impl">{kindOf(s.id)}</span>
                </button>
              </span>
            ))}
          </div>
          <span style={{ width: 64, flex: 'none' }} />
        </div>

        <div className="card-body" style={{ padding: 14, gap: 12 }}>
          <div className="row" style={{ gap: 8 }}>
            <span className="card-title sm">{stageLabel}</span>
            {!overriding && <span className="badge">INHERITED</span>}
            <span className="spacer row" style={{ gap: 7 }}>
              <button
                type="button"
                className={`toggle ${overriding ? 'on' : ''}`}
                onClick={toggleOverride}
                aria-pressed={overriding}
                data-testid={`mapping-${stage}-override`}
              />
              <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>Override here</span>
            </span>
          </div>

          {!overriding && (
            <span className="hint" data-testid={`mapping-${stage}-inherited`}>
              This mapping runs the replication's <strong>{kind || 'unset'}</strong> {stageLabel.toLowerCase()},
              with the settings the replication gives it. Overriding replaces both — this stage's Kind
              and its settings together, never half of each.
            </span>
          )}

          {overriding && (
            <>
              <Field label={`${stageLabel} implementation`}>
                <select
                  className="select"
                  value={kind}
                  onChange={(e) => setStageValue({ kind: e.target.value })}
                  data-testid={`mapping-${stage}-kind-select`}
                >
                  {!known && kind && <option value={kind}>{kind} — not offered by this driver</option>}
                  {kinds.map((o) => (
                    <option key={o.kind} value={o.kind}>{o.note ? `${o.kind} — ${o.note}` : o.kind}</option>
                  ))}
                </select>
              </Field>

              {editable.length > 0 && (
                <ParameterForm
                  parameters={editable}
                  values={effective?.options ?? {}}
                  onChange={(next) => setStageValue({ options: next })}
                  options={{ script: scripts?.map((s) => s.manifest.name) ?? [] }}
                  testIdPrefix={`mapping-${stage}-options`}
                />
              )}
              {declared.length === 0 && (
                <KeyValueTable
                  value={effective?.options ?? {}}
                  onChange={(next) => setStageValue({ options: next })}
                  addLabel="Setting name"
                  testId={`mapping-${stage}-options`}
                />
              )}
            </>
          )}

          {/* Shown whether or not the writer stage is overridden, because the natural key is a
              question about *this table* either way: a mapping inheriting the replication's Scd2
              writer still derives, and still has to be able to say otherwise. */}
          {stage === 'writer' && versionsRows(kindOf('writer')) && inherited && (
            <NaturalKeyField
              replicationName={replicationName}
              mappingName={mappingName}
              value={overrides.writerOverride?.options?.[NATURAL_KEY] ?? ''}
              stated={stated}
              onChange={setNaturalKey}
            />
          )}

          <span className="hint">Save mapping, at the top of this screen, commits to config history.</span>
        </div>
      </div>
    </>
  )
}

/**
 * The derived natural key, and the option of saying otherwise.
 *
 * Read-only by default and showing what *would* be derived, rather than an empty box the operator has
 * to fill in from a schema they would have to go and look up. The derivation comes from the server,
 * through the same helper a run uses, so what this says will happen is what happens.
 */
function NaturalKeyField({ replicationName, mappingName, value, stated, onChange }: {
  replicationName: string
  mappingName: string | undefined
  value: string
  /** Null clears the override and goes back to deriving. */
  onChange: (next: string | null) => void
  stated: boolean
}) {
  const { data: derived, error } = useInferredNaturalKey(replicationName, mappingName, !stated)

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }} data-testid="mapping-natural-key">
      <div className="row" style={{ gap: 8 }}>
        <span className="card-title sm">Natural key</span>
        {!stated && <span className="badge">AUTO-DERIVED</span>}
        <span className="spacer row" style={{ gap: 7 }}>
          <button
            type="button"
            className={`toggle ${stated ? 'on' : ''}`}
            // Overriding starts from the derived columns, so the field opens saying what it was
            // already going to do rather than empty.
            onClick={() => onChange(stated ? null : (derived?.columns.join(', ') ?? ''))}
            aria-pressed={stated}
            data-testid="mapping-natural-key-override"
          />
          <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>Enter it by hand</span>
        </span>
      </div>

      {stated ? (
        <input
          className="input"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder="Column, Column"
          data-testid="mapping-natural-key-input"
        />
      ) : !mappingName ? (
        <span className="hint" data-testid="mapping-natural-key-unsaved">
          Save the mapping and this will show the key derived from its source's primary key.
        </span>
      ) : error || derived?.problem ? (
        <span className="banner warn" role="alert" data-testid="mapping-natural-key-problem">
          {derived?.problem ?? 'The source table could not be read, so nothing could be derived yet.'}
        </span>
      ) : (
        <span className="input" style={{ display: 'flex', alignItems: 'center', background: 'var(--sunken)', borderColor: 'var(--chrome-edge)', color: 'var(--ink-4)', fontFamily: 'var(--mono)', fontSize: 12 }} data-testid="mapping-natural-key-derived">
          {derived ? derived.columns.join(', ') : '…'}
        </span>
      )}

      <span className="hint">
        The column(s) that identify a row across its versions — the business key, not the surrogate
        key this writer generates. Derived from the source table's primary key, translated through this
        mapping's columns. Enter it by hand when identity is not what the primary key says it is.
      </span>
    </div>
  )
}
