import { useEffect, useState } from 'react'
import { Outlet, useOutletContext } from 'react-router-dom'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Field } from '../../components/Field'
import {
  useColumns, useConnections, useDeleteTableMapping, useReplication, useTables,
  useUpsertTableMapping,
} from '../../api/hooks'
import { tableExists } from '../../api/tableExists'
import { canonicalJson } from '../../api/canonicalJson'
import type {
  BatchReloadSegment, ColumnMapping, ColumnMetadata, ProvisioningConfig, ReadIntent, ReplicationTaskConfig,
  ResolvedRef, ScriptBindings, SourceTableSpec, TableMappingConfig, TableSpec,
} from '../../api/types'
import { MappingSide } from './MappingSide'
import { EndpointSidePair } from '../../components/EndpointSidePair'
import { resolveSide } from '../../api/resolveEndpoint'
import { ColumnMappingEditor } from './ColumnMappingEditor'
import { CachedMetadataCard } from './CachedMetadataCard'
import { ScriptBindingsCard } from '../../components/ScriptBindings'
import { NotesPanel } from '../../components/NotesPanel'
import { SubTabs } from '../../components/SubTabs'
import { useMappingTabs } from './mappingTabs'
import { ProvisioningCard } from './ProvisioningCard'
import { DefaultSegmentingCard } from './DefaultSegmentingCard'
import { SourceFilterCard } from './SourceFilterCard'
import { MappingPipelineCard, type PipelineOverrides } from './MappingPipelineCard'
import { isQuerySource, queryOf, withQuery } from './querySource'

/** A new mapping inherits both endpoints — null connection and database — and states only its table. */
const emptySpec: TableSpec = { connectionName: null, database: null, schema: '', table: '' }

/** The provisioning a mapping starts with: inherit both answers rather than opt out of them (phase 68). */
const inheritedProvisioning: ProvisioningConfig =
  { createTargetTableIfMissing: null, alterTargetTableColumnsIfMissingOrChanged: null }

/**
 * Whether two specs point at the same table, for deciding whether a save re-captures that side's
 * cached columns.
 *
 * All four fields, because any of them moving is a different table: the same `dbo.Orders` on another
 * database is not this one. Compared as *configured* rather than as resolved — a mapping that
 * inherits both endpoints has not itself changed when the replication's endpoint moves underneath
 * it, and quietly re-capturing every mapping on such an edit is exactly the silent catch-up the
 * cache exists to prevent. Refresh is how an operator says otherwise.
 */
const samePlace = (a: TableSpec | undefined, b: TableSpec) =>
  !!a && a.connectionName === b.connectionName && a.database === b.database
  && a.schema === b.schema && a.table === b.table

interface Props {
  replicationName: string
  existing?: TableMappingConfig
  /** The section base, `…/mappings` — the tabs and the sibling preview/verify routes hang off it. */
  base: string
  /** The name actually saved, which a rename or a create makes different from the one that was open. */
  onSaved: (mappingName: string) => void
  onRemoved: () => void
  onCancel: () => void
}

/**
 * The mapping editor: what the mapping *is* at the top, and everything about how it behaves in tabs.
 *
 * `EndpointSidePair` and the source filter stay above the tabs, for the reason the replication's
 * endpoints do: every tab below describes what happens between those two tables, and a screen where
 * you cannot see which tables you are configuring without picking a tab first has hidden its own
 * subject.
 *
 * This is a layout route. It owns the form's draft state and hands it to whichever tab is open
 * through the outlet, so moving between tabs does not remount the draft — the same arrangement the
 * replication's layout route uses, for the same reason.
 */
export function TableMappingForm({ replicationName, existing, base, onSaved, onRemoved, onCancel }: Props) {
  const upsert = useUpsertTableMapping(replicationName)
  const del = useDeleteTableMapping(replicationName)
  const { data: task } = useReplication(replicationName)
  const { data: connections } = useConnections()
  const [name, setName] = useState(existing?.name ?? '')
  // Stop inferring the moment somebody types their own. An existing mapping counts as touched: its
  // name is already whatever it is, and rewriting it because the source was adjusted would rename a
  // mapping nobody asked to rename. Same shape as MappingSide's schema-follows-the-pick.
  const [nameTouched, setNameTouched] = useState(existing !== undefined)
  const [source, setSource] = useState<SourceTableSpec>(existing?.sources[0] ?? { ...emptySpec, filter: null })
  const [target, setTarget] = useState<TableSpec>(existing?.targets[0] ?? { ...emptySpec })
  const [columnMappings, setColumnMappings] = useState<ColumnMapping[]>(existing?.columnMappings ?? [])
  const [scripts, setScripts] = useState<ScriptBindings>(structuredClone(existing?.scripts ?? {}))
  const [notes, setNotes] = useState<string | null>(existing?.notes ?? null)
  const [traceTiming, setTraceTiming] = useState(existing?.traceTiming ?? false)
  // Null, not false: a mapping that has never been asked inherits, and a mapping that was asked and
  // said no does not. Starting a new one at false would opt it out of a replication-level default it
  // should have picked up.
  const [provisioning, setProvisioning] = useState<ProvisioningConfig>(
    existing?.provisioning ?? inheritedProvisioning,
  )
  const [defaultSegmenting, setDefaultSegmenting] = useState<BatchReloadSegment[]>(
    structuredClone(existing?.defaultSegmenting ?? []),
  )
  // Null on each, not an empty stage: a mapping that has never been asked inherits the replication's
  // pipeline entirely, which is not the same as one that overrides it with nothing (phase 68).
  const [pipeline, setPipeline] = useState<PipelineOverrides>({
    readerOverride: structuredClone(existing?.readerOverride ?? null),
    cacheOverride: structuredClone(existing?.cacheOverride ?? null),
    writerOverride: structuredClone(existing?.writerOverride ?? null),
    bulkLoadReaderOverride: structuredClone(existing?.bulkLoadReaderOverride ?? null),
    bulkLoadCacheOverride: structuredClone(existing?.bulkLoadCacheOverride ?? null),
    bulkLoadWriterOverride: structuredClone(existing?.bulkLoadWriterOverride ?? null),
  })
  // Null inherits the replication's DefaultReadIntent — see phase 100/102.
  const [defaultReadIntent, setDefaultReadIntent] = useState<ReadIntent | null>(
    existing?.defaultReadIntent ?? null,
  )

  /**
   * Choosing a source fills in the two things that follow from it.
   *
   * The name becomes `schema.table` while it is still untouched, and an **empty** target table becomes
   * the source's table name. Only empty: a target somebody typed is an answer, and overwriting it
   * because the source changed would discard the more deliberate of the two.
   */
  const setSourceSpec = (next: SourceTableSpec) => {
    setSource(next)

    if (!nameTouched && next.schema && next.table)
      setName(`${next.schema}.${next.table}`)

    if (next.table && !target.table)
      setTarget((current) => (current.table ? current : { ...current, table: next.table }))
  }

  /**
   * The columns the source query last returned, for a reader whose source has no catalog to ask.
   *
   * Draft state, not persisted and not fetched: a query's result shape is only knowable by running
   * it, so this is filled in when the operator presses Preview and is empty until they do. Held here
   * rather than inside the source tab so the column-mapping tab — a sibling, remounted whenever the
   * operator switches tabs — can map what the query actually returns.
   */
  const [queryColumns, setQueryColumns] = useState<ColumnMetadata[]>([])
  const querySource = isQuerySource(task, pipeline)

  // What each side actually points at once the replication's endpoints are applied.
  const resolvedSource = resolveSide(task?.endpoints?.source ?? null, source)
  const resolvedTarget = resolveSide(task?.endpoints?.target ?? null, target)

  // Whether the target names a table the database already has. Shared by the picker (which says so)
  // and the column editor (which takes the source's columns when it does not). The same query the
  // picker runs, so this costs nothing.
  const { data: targetTables } = useTables(
    resolvedTarget.connectionName || undefined, resolvedTarget.database || undefined)
  const targetExists = tableExists(targetTables, resolvedTarget.schema, resolvedTarget.table)

  // The Provisioning card plans against the mapping as *saved*, so a target retyped since then is not what
  // it is describing.
  const targetChangedSinceSave = !!existing
    && (existing.targets[0]?.schema !== target.schema || existing.targets[0]?.table !== target.table)

  // **Auto-suggested column mappings, up here rather than in the editor tab.**
  //
  // A same-name match on first load is what makes a new mapping saveable — Save requires at least one
  // column mapping — and the editor is one tab among several now. Left where it was, creating a
  // mapping without opening the Column Mapping tab would leave Save permanently disabled with nothing
  // on screen saying why. These are the same queries the editor runs, keyed identically, so React
  // Query serves both from one fetch.
  // A query source's driver reports no columns at all — deliberately, since a query's rows come from
  // whatever its scanners reach rather than from a catalog. So the preview's result stands in, and
  // the metadata call is not made: it would return an empty list at best and 404 at worst.
  const { data: catalogSourceColumns } = useColumns(
    resolvedSource.connectionName, resolvedSource.database, resolvedSource.schema,
    querySource ? undefined : resolvedSource.table)
  const sourceColumns = querySource ? queryColumns : catalogSourceColumns
  const { data: catalogTargetColumns } = useColumns(
    resolvedTarget.connectionName, resolvedTarget.database, resolvedTarget.schema,
    targetExists === true ? resolvedTarget.table : undefined)
  const targetColumns = targetExists === false ? sourceColumns : catalogTargetColumns

  useEffect(() => {
    if (!targetColumns || !sourceColumns || columnMappings.length > 0) return
    const sourceNames = new Set(sourceColumns.map((c) => c.name))
    const suggested = targetColumns
      .filter((tc) => sourceNames.has(tc.name))
      .map((tc) => ({ sourceColumn: tc.name, targetColumn: tc.name, transform: null }))
    if (suggested.length > 0) setColumnMappings(suggested)
    // Only auto-suggest once, when both column lists first become available and nothing is mapped.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sourceColumns, targetColumns])

  // The tab bar and its provisioning badge, computed by the hook rather than here: Preview SQL and
  // Verify wear the same bar, and one implementation is the only way three screens agree on what it
  // says.
  const tabs = useMappingTabs(replicationName, base, existing?.name)

  // A query source is saveable without a source table, because it has none — its statement is what
  // it reads. Everything else is unchanged: both endpoints still have to resolve (config rejects a
  // mapping whose source database is blank), and there still has to be something mapped.
  const sourceStated = querySource ? queryOf(task, pipeline).trim().length > 0 : Boolean(source.table)

  const canSave = name
    && resolvedSource.connectionName && resolvedSource.database && sourceStated
    && resolvedTarget.connectionName && resolvedTarget.database && target.table
    && columnMappings.length > 0

  /**
   * Whether the draft differs from the mapping as saved. Save commits a change, so with nothing
   * changed the button is disabled rather than a no-op that restamps the document (and, before this,
   * bounced the operator to a different tab for their trouble).
   *
   * Only the fields this form edits are compared, in the same shape and with the same inherit
   * fallbacks the state above is seeded with — so an untouched draft compares equal. Captured column
   * metadata is deliberately absent: a save carries it across, but Refresh on the Column Mapping tab
   * is what changes it, never an edit here. A brand-new mapping has nothing to compare against and is
   * dirty as soon as it is valid.
   */
  const draftShape = canonicalJson({
    name, source, target, columnMappings, scripts, provisioning, defaultSegmenting,
    notes, traceTiming, defaultReadIntent,
    readerOverride: pipeline.readerOverride,
    cacheOverride: pipeline.cacheOverride,
    writerOverride: pipeline.writerOverride,
    bulkLoadReaderOverride: pipeline.bulkLoadReaderOverride,
    bulkLoadCacheOverride: pipeline.bulkLoadCacheOverride,
    bulkLoadWriterOverride: pipeline.bulkLoadWriterOverride,
  })
  const savedShape = existing && canonicalJson({
    name: existing.name,
    source: existing.sources[0] ?? { ...emptySpec, filter: null },
    target: existing.targets[0] ?? { ...emptySpec },
    columnMappings: existing.columnMappings ?? [],
    scripts: existing.scripts ?? {},
    provisioning: existing.provisioning ?? inheritedProvisioning,
    defaultSegmenting: existing.defaultSegmenting ?? [],
    notes: existing.notes ?? null,
    traceTiming: existing.traceTiming ?? false,
    defaultReadIntent: existing.defaultReadIntent ?? null,
    readerOverride: existing.readerOverride ?? null,
    cacheOverride: existing.cacheOverride ?? null,
    writerOverride: existing.writerOverride ?? null,
    bulkLoadReaderOverride: existing.bulkLoadReaderOverride ?? null,
    bulkLoadCacheOverride: existing.bulkLoadCacheOverride ?? null,
    bulkLoadWriterOverride: existing.bulkLoadWriterOverride ?? null,
  })
  const dirty = !existing || draftShape !== savedShape

  /**
   * The cached column metadata this save should carry — phase 90.
   *
   * **Captured from the lists this form already fetched, never from a query of its own.** The
   * pickers above and the column-mapping tab both run `useColumns` on the resolved endpoints, keyed
   * identically, so React Query has already served the answer; writing it into the mapping costs
   * nothing beyond the bytes.
   *
   * **And only when it is a capture.** A side whose table has not moved since the mapping was
   * loaded sends back what is stored, so re-saving to fix a typo in a note does not restamp the
   * cache with today's date — the whole design is that the cache goes stale visibly rather than
   * quietly catching up. A side with nothing cached yet always captures: there is no picture to
   * preserve, and one is what the operator came for. The server holds both halves of this rule too
   * (`MappingMetadataCapture`), because the SPA is not the only thing that can `PUT`.
   */
  const captureFor = (
    fetched: ColumnMetadata[] | undefined,
    cached: ColumnMetadata[] | undefined,
    tableChanged: boolean,
  ) => (fetched && (tableChanged || !cached || cached.length === 0) ? fetched : cached ?? [])

  const sourceTableChanged = !!existing && !samePlace(existing.sources[0], source)
  const targetTableChanged = !!existing && !samePlace(existing.targets[0], target)

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    await upsert.mutateAsync({
      mappingName: name,
      mapping: {
        // A save replaces the whole document, so anything this form does not edit has to be carried
        // across explicitly — verification checks and hooks are both edited elsewhere, and were
        // being dropped by a save from here.
        ...existing,
        name, sources: [source], targets: [target], columnMappings, scripts, provisioning,
        defaultSegmenting, notes, traceTiming, defaultReadIntent, ...pipeline,
        sourceColumns: captureFor(sourceColumns, existing?.sourceColumns, sourceTableChanged),
        // `catalogTargetColumns`, not `targetColumns` — the latter falls back to the *source's*
        // columns for a target that does not exist yet, which is right for an editor about to
        // create that table and wrong for a cache claiming to describe the target as it is.
        targetColumns: captureFor(catalogTargetColumns, existing?.targetColumns, targetTableChanged),
      },
    })
    onSaved(name)
  }

  return (
    <form onSubmit={save} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
      <div className="page-head">
        <h2 className="page-title mono">{existing ? existing.name : 'New table mapping'}</h2>
        {existing && <span className="badge badge-accent">MAPPED</span>}
        <div className="right">
          {/* Preview SQL and Verify were buttons here; they are tabs now, beside the rest of what
              this mapping is, rather than two links wearing a different hat from everything else. */}
          <button type="button" className="btn" onClick={onCancel}>Cancel</button>
          {existing && (
            <button
              type="button"
              className="btn btn-danger"
              onClick={async () => { await del.mutateAsync(existing.name); onRemoved() }}
              data-testid={`delete-mapping-${existing.name}`}
            >
              Delete
            </button>
          )}
          <button type="submit" className="btn btn-primary" disabled={!canSave || !dirty || upsert.isPending} data-testid="save-mapping-button">
            {upsert.isPending ? 'Saving…' : 'Save mapping'}
          </button>
        </div>
      </div>

      <ErrorBanner error={upsert.error ?? del.error} />

      {!existing && (
        <div className="card">
          <div className="card-head"><span className="card-title">Mapping</span></div>
          <div className="card-body">
            <Field label="Name">
                <input
                className="input"
                required
                value={name}
                onChange={(e) => { setName(e.target.value); setNameTouched(true) }}
                data-testid="mapping-name-input"
              />
            </Field>
          </div>
        </div>
      )}

      <EndpointSidePair
        source={
          <MappingSide
            side="source"
            label="Source"
            inherited={task?.endpoints?.source ?? null}
            spec={source}
            onChange={(v) => setSourceSpec({ ...v, filter: source.filter })}
            testIdPrefix="source"
            query={querySource ? {
              value: queryOf(task, pipeline),
              onChange: (next) => setPipeline(withQuery(task, pipeline, next)),
              onColumns: setQueryColumns,
            } : undefined}
          />
        }
        target={
          <MappingSide
            side="target"
            label="Target"
            inherited={task?.endpoints?.target ?? null}
            spec={target}
            onChange={setTarget}
            testIdPrefix="target"
            allowNewTable
          />
        }
      />

      <SourceFilterCard
        filter={source.filter ?? null}
        onChange={(filter) => setSource({ ...source, filter })}
      />

      <SubTabs
        base={existing ? `${base}/${encodeURIComponent(existing.name)}` : `${base}/new`}
        tabs={tabs}
        testId="mapping-subtabs"
      />

      <div className="subtab-panel">
        <Outlet
          context={{
            replicationName, existing, resolvedSource, resolvedTarget, targetExists,
            targetChangedSinceSave, connections: connections ?? [], task,
            columnMappings, setColumnMappings,
            sourceColumns, querySource,
            scripts, setScripts,
            defaultSegmenting, setDefaultSegmenting,
            provisioning, setProvisioning,
            pipeline, setPipeline,
            defaultReadIntent, setDefaultReadIntent,
            notes, setNotes,
            traceTiming, setTraceTiming,
            target,
          } satisfies MappingEditorContext}
        />
      </div>
    </form>
  )
}

/** What each of the mapping editor's tabs is handed — the one draft, held by the layout above them. */
export interface MappingEditorContext {
  replicationName: string
  existing: TableMappingConfig | undefined
  resolvedSource: ResolvedRef
  resolvedTarget: ResolvedRef
  targetExists: boolean | undefined
  targetChangedSinceSave: boolean
  connections: { name: string; scripts?: ScriptBindings }[]
  task: ReplicationTaskConfig | undefined
  columnMappings: ColumnMapping[]
  setColumnMappings: (next: ColumnMapping[]) => void
  /** The source's columns as this form resolved them — the catalog's, or the query preview's for a
   * source that has no catalog. Undefined while the catalog call is still in flight. */
  sourceColumns: ColumnMetadata[] | undefined
  /** Whether this mapping's reader is configured by a query rather than by a table. */
  querySource: boolean
  scripts: ScriptBindings
  setScripts: (next: ScriptBindings) => void
  defaultSegmenting: BatchReloadSegment[]
  setDefaultSegmenting: (next: BatchReloadSegment[]) => void
  provisioning: ProvisioningConfig
  setProvisioning: (next: ProvisioningConfig) => void
  pipeline: PipelineOverrides
  setPipeline: (next: PipelineOverrides) => void
  defaultReadIntent: ReadIntent | null
  setDefaultReadIntent: (next: ReadIntent | null) => void
  notes: string | null
  setNotes: (next: string | null) => void
  traceTiming: boolean
  setTraceTiming: (next: boolean) => void
  target: TableSpec
}

export function MappingNotesTab() {
  const { notes, setNotes } = useOutletContext<MappingEditorContext>()
  return (
    <NotesPanel value={notes} onChange={setNotes} subject="this table mapping" testId="mapping-notes" />
  )
}

export function ColumnMappingTab() {
  const {
    replicationName, existing, resolvedTarget, columnMappings, setColumnMappings,
    targetExists, sourceColumns, querySource,
  } = useOutletContext<MappingEditorContext>()

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
      <ColumnMappingEditor
        replicationName={replicationName}
        sourceColumns={sourceColumns}
        querySource={querySource}
        // The saved name, not the draft one: the inferred-type endpoint reads config off disk, and a
        // mapping being renamed in this form does not exist under its new name until it is saved.
        mappingName={existing?.name}
        target={resolvedTarget}
        mappings={columnMappings}
        onChange={setColumnMappings}
        targetExists={targetExists}
      />
      {/* Below the editor rather than on a tab of its own: the cache is the same five facts about
          the same two tables that this grid is showing, and a screen an operator has to go looking
          for is one they will not think to refresh. */}
      <CachedMetadataCard replicationName={replicationName} existing={existing} />
    </div>
  )
}

export function MappingTransformsTab() {
  const { connections, task, resolvedSource, scripts, setScripts } = useOutletContext<MappingEditorContext>()
  const sourceConnection = connections.find((c) => c.name === resolvedSource.connectionName)

  return (
    <ScriptBindingsCard
      bindings={scripts}
      // The mapping is the most specific level, so what it inherits is the replication's binding if
      // it has one and the source connection's otherwise — the same order the server resolves in.
      inherited={{ ...(sourceConnection?.scripts ?? {}), ...(task?.scripts ?? {}) }}
      level="mapping"
      onChange={setScripts}
    />
  )
}

export function MappingSegmentingTab() {
  const { task, defaultSegmenting, setDefaultSegmenting } = useOutletContext<MappingEditorContext>()
  return (
    <DefaultSegmentingCard
      segments={defaultSegmenting}
      strategies={task?.segmentingStrategies ?? []}
      onChange={setDefaultSegmenting}
    />
  )
}

export function MappingProvisioningTab() {
  const {
    replicationName, existing, provisioning, setProvisioning, task, targetChangedSinceSave,
    targetExists, target,
  } = useOutletContext<MappingEditorContext>()

  // Only a saved mapping has a name the provisioning API can plan against.
  if (!existing) {
    return (
      <div className="card">
        <div className="card-body">
          <span className="hint" data-testid="provisioning-after-save-hint">
            {targetExists === false && target.table
              ? <><strong>{target.schema || 'dbo'}.{target.table}</strong> does not exist yet. Save the
                mapping and this tab will show the <code>CREATE TABLE</code> it would run.</>
              : <>Save the mapping and this tab will show what it would run against each side.</>}
          </span>
        </div>
      </div>
    )
  }

  return (
    <ProvisioningCard
      replicationName={replicationName}
      mappingName={existing.name}
      provisioning={provisioning}
      inherited={task?.provisioning}
      onChangeProvisioning={setProvisioning}
      stale={targetChangedSinceSave}
    />
  )
}

/**
 * Which reader, staging provider and writer this table runs — phase 68.
 *
 * **Its own tab rather than a card on one of the others**, mirroring the replication's Overview,
 * because it is the same question asked one level down. An operator who has set the pipeline on the
 * replication and wants to know why one table behaves differently looks for the word they already
 * know, in the place the rest of this mapping's behaviour is configured.
 */
export function MappingPipelineTab() {
  const {
    replicationName, existing, task, resolvedSource, resolvedTarget, pipeline, setPipeline,
    defaultReadIntent, setDefaultReadIntent,
  } = useOutletContext<MappingEditorContext>()

  return (
    <MappingPipelineCard
      replicationName={replicationName}
      mappingName={existing?.name}
      task={task}
      source={resolvedSource}
      target={resolvedTarget}
      overrides={pipeline}
      onChange={setPipeline}
      defaultReadIntent={defaultReadIntent}
      onChangeDefaultReadIntent={setDefaultReadIntent}
    />
  )
}

/**
 * What this mapping measures about its own passes — phase 62, surfacing phase 59's opt-in trace.
 *
 * **Its own tab rather than folded into one of the others**, and the reason is what the other tabs
 * are: Column Mapping, Custom Transforms, Bulk Load and Provisioning all describe what this
 * mapping *is* and what it will do. Tracing describes how it is *observed* — it changes no behaviour
 * and produces no different result, only numbers about the pass. It sits beside Preview SQL and
 * Verify, which are the other two answers to "what is this mapping actually doing", and it is where
 * the aggregate view phase 59 deliberately deferred would go when somebody builds it.
 *
 * No INHERITED badge: mapping-level only, per phase 59. Tracing is something an operator turns on for
 * the one table behaving oddly, which is not a thing to inherit from a replication.
 */
export function MappingDiagnosticsTab() {
  const { traceTiming, setTraceTiming } = useOutletContext<MappingEditorContext>()

  return (
    <div className="card" data-testid="mapping-diagnostics">
      <div className="card-head">
        <span className="card-title">Diagnostics</span>
        <span className="card-note">what this mapping records about its own passes</span>
      </div>
      <div className="card-body" style={{ gap: 12 }}>
        <div className="row" style={{ gap: 9 }}>
          <button
            type="button"
            className={`toggle ${traceTiming ? 'on' : ''}`}
            onClick={() => setTraceTiming(!traceTiming)}
            aria-pressed={traceTiming}
            data-testid="trace-timing-toggle"
          />
          <span style={{ font: '500 12.5px var(--ui)', color: 'var(--ink-2)' }}>Trace stage timing</span>
        </div>
        <span className="hint">
          Records how long the reader, staging and writer each took, onto every run this mapping
          produces — visible by expanding that run in Run history. Off means <em>not measured</em>
          rather than measured and discarded: the reader's rows are only wrapped when this is on, so a
          mapping that never asked pays nothing for it.
        </span>
        <span className="hint">
          Worth turning on for one table that is behaving oddly, and worth turning off again
          afterwards. Runs already traced keep their numbers.
        </span>
      </div>
    </div>
  )
}
