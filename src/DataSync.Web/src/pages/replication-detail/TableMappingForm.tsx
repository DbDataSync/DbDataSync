import { useEffect, useState } from 'react'
import { Outlet, useOutletContext } from 'react-router-dom'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Field } from '../../components/Field'
import {
  useColumns, useConnections, useDeleteTableMapping, useProvisioning, useReplication, useTables,
  useUpsertTableMapping,
} from '../../api/hooks'
import { tableExists } from '../../api/tableExists'
import type {
  BatchReloadSegment, ColumnMapping, ProvisioningConfig, ReplicationTaskConfig, ResolvedRef,
  ScriptBindings, SourceTableSpec, TableMappingConfig, TableSpec,
} from '../../api/types'
import { MappingSide } from './MappingSide'
import { EndpointSidePair } from '../../components/EndpointSidePair'
import { resolveSide } from '../../api/resolveEndpoint'
import { CodeEditor } from '../../components/CodeEditor'
import { ColumnMappingEditor } from './ColumnMappingEditor'
import { ScriptBindingsCard } from '../../components/ScriptBindings'
import { NotesPanel } from '../../components/NotesPanel'
import { SubTabs } from '../../components/SubTabs'
import { mappingTabs } from './mappingTabs'
import { ProvisioningCard } from './ProvisioningCard'
import { DefaultSegmentingCard } from './DefaultSegmentingCard'

/** A new mapping inherits both endpoints — null connection and database — and states only its table. */
const emptySpec: TableSpec = { connectionName: null, database: null, schema: '', table: '' }

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
  const [provisioning, setProvisioning] = useState<ProvisioningConfig>(
    existing?.provisioning
      // Null, not false: a mapping that has never been asked inherits, and a mapping that was asked
      // and said no does not. Starting a new one at false would opt it out of a replication-level
      // default it should have picked up.
      ?? { createTargetTableIfMissing: null, alterTargetTableColumnsIfMissingOrChanged: null },
  )
  const [defaultSegmenting, setDefaultSegmenting] = useState<BatchReloadSegment[]>(
    structuredClone(existing?.defaultSegmenting ?? []),
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

  // What each side actually points at once the replication's endpoints are applied.
  const resolvedSource = resolveSide(task?.endpoints?.source ?? null, source)
  const resolvedTarget = resolveSide(task?.endpoints?.target ?? null, target)

  // Whether the target names a table the database already has. Shared by the picker (which says so)
  // and the column editor (which takes the source's columns when it does not). The same query the
  // picker runs, so this costs nothing.
  const { data: targetTables } = useTables(
    resolvedTarget.connectionName || undefined, resolvedTarget.database || undefined)
  const targetExists = tableExists(targetTables, resolvedTarget.schema, resolvedTarget.table)

  // The Setup card plans against the mapping as *saved*, so a target retyped since then is not what
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
  const { data: sourceColumns } = useColumns(
    resolvedSource.connectionName, resolvedSource.database, resolvedSource.schema, resolvedSource.table)
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

  // The tab bar shows the badge whichever tab is open, so it asks for the plans itself rather than
  // only the Provisioning tab's card doing so. The same query, keyed the same way — React Query
  // serves both from one fetch.
  const { data: plans } = useProvisioning(replicationName, existing?.name)
  const pendingSteps = (plans?.source.steps.length ?? 0) + (plans?.target.steps.length ?? 0)

  const canSave = name
    && resolvedSource.connectionName && resolvedSource.database && source.table
    && resolvedTarget.connectionName && resolvedTarget.database && target.table
    && columnMappings.length > 0

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
        defaultSegmenting, notes, traceTiming,
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
          <button type="submit" className="btn btn-primary" disabled={!canSave || upsert.isPending} data-testid="save-mapping-button">
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
        tabs={mappingTabs(base, existing?.name, pendingSteps)}
        testId="mapping-subtabs"
      />

      <div className="subtab-panel">
        <Outlet
          context={{
            replicationName, existing, resolvedSource, resolvedTarget, targetExists,
            targetChangedSinceSave, connections: connections ?? [], task,
            columnMappings, setColumnMappings,
            scripts, setScripts,
            defaultSegmenting, setDefaultSegmenting,
            provisioning, setProvisioning,
            notes, setNotes,
            traceTiming, setTraceTiming,
            target,
          } satisfies MappingEditorContext}
        />
      </div>
    </form>
  )
}

/**
 * The mapping's source filter — its own row beneath both sides rather than stacked under Source
 * alone. It belongs to the source, but hanging it off one card made the two sides different heights
 * and stopped them being comparable at a glance, which is the whole reason to put them next to each
 * other.
 *
 * **Collapsed when empty**, the convention `ScriptBindingsCard` established: most mappings read the
 * whole table, and a card holding an editor for a predicate nobody wrote is space spent on the
 * exception. It opens by itself when there *is* a filter, because then it is describing behaviour
 * rather than offering it — and says so on the collapsed heading, since a filter silently narrowing
 * what a replication reads is exactly the thing somebody debugging missing rows needs to see first.
 */
function SourceFilterCard({ filter, onChange }: {
  filter: string | null
  onChange: (next: string | null) => void
}) {
  const [expanded, setExpanded] = useState(false)
  const applied = !!filter?.trim()
  const open = expanded || applied

  return (
    <div className="card" data-testid="source-filter-card">
      <div className="card-head">
        <button
          type="button"
          className="btn-link"
          onClick={() => setExpanded(!open)}
          aria-expanded={open}
          data-testid="source-filter-toggle"
        >
          {open ? '▾' : '▸'} Source filter
        </button>
        {applied
          ? <span className="badge badge-accent" data-testid="source-filter-applied-pill">FILTERS APPLIED</span>
          : <span className="card-note">optional SQL predicate — the whole table is read without one</span>}
      </div>
      {open && (
        <div className="card-body">
          <Field label="Source filter — optional SQL predicate">
            {/* An editor rather than an input: a predicate that narrows a real table outgrows forty
                visible characters quickly, and this one is spliced into the reader's WHERE clause
                verbatim. */}
            <CodeEditor
              value={filter ?? ''}
              language="sql"
              onChange={(next) => onChange(next.trim() ? next : null)}
              minLines={2}
              maxLines={8}
              testId="source-filter-editor"
            />
          </Field>
        </div>
      )}
    </div>
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
  scripts: ScriptBindings
  setScripts: (next: ScriptBindings) => void
  defaultSegmenting: BatchReloadSegment[]
  setDefaultSegmenting: (next: BatchReloadSegment[]) => void
  provisioning: ProvisioningConfig
  setProvisioning: (next: ProvisioningConfig) => void
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
    replicationName, existing, resolvedSource, resolvedTarget, columnMappings, setColumnMappings,
    targetExists,
  } = useOutletContext<MappingEditorContext>()

  return (
    <ColumnMappingEditor
      replicationName={replicationName}
      // The saved name, not the draft one: the inferred-type endpoint reads config off disk, and a
      // mapping being renamed in this form does not exist under its new name until it is saved.
      mappingName={existing?.name}
      source={resolvedSource}
      target={resolvedTarget}
      mappings={columnMappings}
      onChange={setColumnMappings}
      targetExists={targetExists}
    />
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
 * What this mapping measures about its own passes — phase 62, surfacing phase 59's opt-in trace.
 *
 * **Its own tab rather than folded into one of the others**, and the reason is what the other tabs
 * are: Column Mapping, Custom Transforms, Reload Segmenting and Provisioning all describe what this
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
