import { useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Link, Outlet, useOutletContext } from 'react-router-dom'
import { Field } from '../../components/Field'
import { KeyValueTable } from '../../components/KeyValueTable'
import { ParameterForm } from '../../components/ParameterForm'
import { EndpointsCard } from './EndpointsCard'
import { InheritableToggle } from '../../components/InheritableToggle'
import { ScriptBindingsCard } from '../../components/ScriptBindings'
import { SegmentingStrategiesCard } from './SegmentingStrategiesCard'
import { NotesPanel } from '../../components/NotesPanel'
import { SubTabs, type SubTab } from '../../components/SubTabs'
import { readerNotes } from '../../api/readerNotes'
import { versionsRows, withoutNaturalKey } from './naturalKey'
import { useConnections, useReplication, useReplicationCapabilities, useScripts, useTableMappings } from '../../api/hooks'
import type { ParameterDescriptor, ProvisioningConfig, ReplicationTaskConfig } from '../../api/types'

type Stage = 'reader' | 'cache' | 'writer'

const STAGES: { id: Stage; label: string }[] = [
  { id: 'reader', label: 'Reader' },
  { id: 'cache', label: 'Staging' },
  { id: 'writer', label: 'Writer' },
]

/** Notes is the index — no segment of its own, so `/replications/:name/overview` opens it. */
const TABS: SubTab[] = [
  { path: null, label: 'Notes', testId: 'overview-tab-notes' },
  { path: 'pipeline', label: 'Pipeline', testId: 'overview-tab-pipeline' },
  { path: 'provisioning', label: 'Target Provisioning', testId: 'overview-tab-provisioning' },
  { path: 'transforms', label: 'Custom Transforms', testId: 'overview-tab-transforms' },
  { path: 'segmenting', label: 'Backfill', testId: 'overview-tab-segmenting' },
]

/**
 * The replication's Overview: Source/Target on top, and everything else in a tabbed area beneath it.
 *
 * The endpoints stay outside the tabs because every one of the tabs is about what happens *between*
 * those two endpoints — a pipeline stage, a provisioning default, a transform. Putting them in a tab
 * of their own would mean the answer to "what does this replication connect" was one click away on a
 * screen whose whole subject is that pair.
 *
 * The draft is still the layout route's (phase 46), and the sub-routes read and write the same object,
 * so moving between tabs keeps an unfinished edit exactly as switching top-level tabs already did.
 */
export function OverviewPanel({ replicationName, draft, setDraft }: {
  replicationName: string
  /** Owned by the layout route since phase 46, so an edit survives a look at another tab. */
  draft: ReplicationTaskConfig
  setDraft: (next: ReplicationTaskConfig) => void
}) {
  const { data: mappings } = useTableMappings(replicationName)
  const { error } = useReplication(replicationName)
  const base = `/replications/${encodeURIComponent(replicationName)}/overview`

  return (
    <div className="pane">
      <ErrorBanner error={error} />

      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        <EndpointsCard
          endpoints={draft.endpoints ?? { source: null, target: null }}
          mappingCount={mappings?.length}
          onChange={(endpoints) => setDraft({ ...draft, endpoints })}
        />

        <SubTabs base={base} tabs={TABS} testId="overview-subtabs" />

        <div className="subtab-panel">
          <Outlet context={{ replicationName, draft, setDraft } satisfies OverviewOutletContext} />
        </div>
      </div>
    </div>
  )
}

/** What each of the Overview's tabs is handed. The same draft object throughout — see the class doc. */
export interface OverviewOutletContext {
  replicationName: string
  draft: ReplicationTaskConfig
  setDraft: (next: ReplicationTaskConfig) => void
}

export function OverviewNotesTab() {
  const { draft, setDraft } = useOutletContext<OverviewOutletContext>()
  return (
    <NotesPanel
      value={draft.notes}
      onChange={(notes) => setDraft({ ...draft, notes })}
      subject="this replication"
      testId="replication-notes"
    />
  )
}

/**
 * The design turns the pipeline into three selectable stages — Reader → Staging → Writer — with the
 * selected stage's implementation and its settings below. That is a better fit for the data than the
 * three stacked Kind pickers plus raw-JSON textareas it replaces: a stage's Kind and its options
 * belong together, and the options are a string dictionary, which a two-column table states plainly.
 */
export function PipelineTab() {
  const { replicationName, draft, setDraft } = useOutletContext<OverviewOutletContext>()
  const { data: mappings } = useTableMappings(replicationName)
  const { data: scripts } = useScripts()
  const mappingsBase = `/replications/${encodeURIComponent(replicationName)}/mappings`
  const capabilities = useReplicationCapabilities(replicationName)

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

  // SCD2 closes a version when a key is deleted, which needs a reader that says so. The reader
  // declares whether it can (IChangeReader.DetectsDeletes) rather than this string-matching Kinds.
  const historizingWithoutDeletes =
    draft.changeProcessing.writer.kind === 'Scd2'
    && capabilities.readers.find((r) => r.kind === draft.changeProcessing.reader.kind)?.detectsDeletes === false

  // The natural key is not a question this level can answer since phase 68: one replication syncs
  // many tables, and each has its own. Declared settings minus that one, and static text in its place.
  const historizing = stage === 'writer' && versionsRows(draft.changeProcessing.writer.kind)

  const current = draft.changeProcessing[stage]
  const declared = parametersFor(stage, current.kind)
  const editable = historizing ? withoutNaturalKey(declared) : declared
  const options = kindsFor(stage)
  const known = options.some((o) => o.kind === current.kind)
  const stageLabel = STAGES.find((s) => s.id === stage)!.label

  return (
    <>
      <ErrorBanner error={capabilities.error} />
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

          {/* An informed choice, not a validation error. SCD Type 2 keeps history by versioning
              each key and closing the old version when it changes — and closing one for a *deleted*
              key needs a reader that reports deletes. Paired with one that cannot, a row that
              disappears at the source stays "current" here forever, which is a real configuration
              for a source that never deletes and a silent wrong answer for one that does. */}
          {stage === 'writer' && historizingWithoutDeletes && (
            <span className="banner warn" role="alert" data-testid="scd2-delete-blind-warning">
              The <strong>{draft.changeProcessing.reader.kind}</strong> reader cannot report deletes,
              so a row deleted at the source will stay marked current in this target forever. That is
              correct for a source that never deletes rows, and wrong for one that does.
            </span>
          )}

          {/* No field to type one into, deliberately. A replication-wide natural key was only ever
              correct for a replication that syncs one table; each mapping derives its own from that
              table's primary key, and a mapping's own Pipeline tab is where one can still be stated
              by hand. See phase 68. */}
          {historizing && (
            <span className="hint" data-testid="natural-key-auto-derived">
              <strong>Natural key:</strong> auto-derived from each mapping's primary key. Which columns
              identify a row across its versions differs per table, so it is answered on each table
              mapping's own Pipeline tab — where it can also be overridden by hand.
            </span>
          )}

          {/* The declared settings, plus whatever else is already in the bag. A Kind that
              declares nothing still gets the free-form table, because an option a driver reads
              but has not declared is still an option somebody set. */}
          {editable.length > 0 && (
            <ParameterForm
              parameters={editable}
              values={current.options}
              onChange={(next) => setStageValue(stage, { options: next })}
              options={{ script: scripts?.map((s) => s.manifest.name) ?? [] }}
              testIdPrefix={`${stage}-options`}
            />
          )}
          {/* The unfiltered count, not the rendered one: a Kind whose only declared setting this tab
              renders itself has not become a Kind that declares nothing, and offering the free-form
              table here would put the field back that the line above just removed. */}
          {declared.length === 0 && (
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
    </>
  )
}

/**
 * The default every mapping under this replication takes unless it says otherwise — parallel to how
 * the endpoints card sets the replication-level endpoints. One answer here beats the same checkbox
 * ticked on forty mappings.
 */
export function TargetProvisioningTab() {
  const { draft, setDraft } = useOutletContext<OverviewOutletContext>()

  // A replication that has never been asked has no provisioning block at all, and both settings read
  // as "nobody has said" — which resolves to off, and is not the same as having said no.
  const provisioningDraft: ProvisioningConfig = draft.provisioning
    ?? { createTargetTableIfMissing: null, alterTargetTableColumnsIfMissingOrChanged: null }

  return (
    <div className="card">
      <div className="card-head">
        <span className="card-title">Target provisioning</span>
        <span className="card-note">the default for every table mapping in this replication</span>
      </div>
      <div className="card-body" style={{ gap: 14 }}>
        <InheritableToggle
          label="Create target table if missing"
          description="Creates the table only when it does not exist. Never alters one that does."
          value={draft.provisioning?.createTargetTableIfMissing ?? null}
          inherited={false}
          onChange={(next) => setDraft({
            ...draft,
            provisioning: { ...provisioningDraft, createTargetTableIfMissing: next },
          })}
          testId="task-provisioning-create"
        />
        <div className="divider" />
        <InheritableToggle
          label="Alter target columns if missing or changed"
          description="Adds a mapped column the target lacks and changes one whose type no longer matches. Never drops a column."
          value={draft.provisioning?.alterTargetTableColumnsIfMissingOrChanged ?? null}
          inherited={false}
          onChange={(next) => setDraft({
            ...draft,
            provisioning: { ...provisioningDraft, alterTargetTableColumnsIfMissingOrChanged: next },
          })}
          testId="task-provisioning-alter"
        />
      </div>
    </div>
  )
}

export function CustomTransformsTab() {
  const { replicationName, draft, setDraft } = useOutletContext<OverviewOutletContext>()
  const { data: task } = useReplication(replicationName)
  // These slots are source-side, so what a replication inherits is whatever its *source* connection
  // binds. Read from the saved task rather than the draft: changing the endpoint mid-edit should not
  // silently repoint what the INHERITED badge is describing.
  const { data: connections } = useConnections()
  const sourceConnection = connections?.find((c) => c.name === task?.endpoints?.source?.connectionName)

  return (
    <ScriptBindingsCard
      bindings={draft.scripts ?? {}}
      // What a mapping under this replication would inherit if the replication said nothing:
      // whatever the *source* connection binds, since these slots are source-side.
      inherited={sourceConnection?.scripts ?? {}}
      level="replication"
      onChange={(scripts) => setDraft({ ...draft, scripts })}
    />
  )
}

/**
 * The replication's named segmenting strategies — phase 61.
 *
 * **Its own tab, named exactly as the mapping editor's is.** The two are the halves of one idea: this
 * defines the strategies, and a mapping's Backfill tab chooses among them. An operator who
 * has seen "Backfill" on a mapping and wants to know where the names come from will look for
 * the same words here, and finding them somewhere else called something else is the version of this
 * that wastes their afternoon.
 *
 * The phase doc predates the Overview being tabbed at all and asked for a card in the old flat stack;
 * neither Pipeline (which is reader/staging/writer) nor Custom Transforms (which is script *bindings*,
 * a different thing entirely) is where this belongs.
 */
export function SegmentingStrategiesTab() {
  const { replicationName, draft, setDraft } = useOutletContext<OverviewOutletContext>()
  return (
    <SegmentingStrategiesCard
      replicationName={replicationName}
      strategies={draft.segmentingStrategies ?? []}
      onChange={(segmentingStrategies) => setDraft({ ...draft, segmentingStrategies })}
    />
  )
}
