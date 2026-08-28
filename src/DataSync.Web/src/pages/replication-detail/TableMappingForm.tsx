import { useState } from 'react'
import { Link } from 'react-router-dom'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Field } from '../../components/Field'
import { useConnections, useDeleteTableMapping, useReplication, useTables, useUpsertTableMapping } from '../../api/hooks'
import { tableExists } from '../../api/tableExists'
import type { ColumnMapping, ScriptBindings, SourceTableSpec, TableMappingConfig, TableSpec } from '../../api/types'
import { MappingSide } from './MappingSide'
import { EndpointSidePair } from '../../components/EndpointSidePair'
import { resolveSide } from '../../api/resolveEndpoint'
import { CodeEditor } from '../../components/CodeEditor'
import { ColumnMappingEditor } from './ColumnMappingEditor'
import { ScriptBindingsCard } from '../../components/ScriptBindings'
import { ProvisioningCard } from './ProvisioningCard'

/** A new mapping inherits both endpoints — null connection and database — and states only its table. */
const emptySpec: TableSpec = { connectionName: null, database: null, schema: '', table: '' }

interface Props {
  replicationName: string
  existing?: TableMappingConfig
  /** The name actually saved, which a rename or a create makes different from the one that was open. */
  onSaved: (mappingName: string) => void
  onRemoved: () => void
  onCancel: () => void
}

export function TableMappingForm({ replicationName, existing, onSaved, onRemoved, onCancel }: Props) {
  const upsert = useUpsertTableMapping(replicationName)
  const del = useDeleteTableMapping(replicationName)
  const { data: task } = useReplication(replicationName)
  const { data: connections } = useConnections()
  const [name, setName] = useState(existing?.name ?? '')
  const [source, setSource] = useState<SourceTableSpec>(existing?.sources[0] ?? { ...emptySpec, filter: null })
  const [target, setTarget] = useState<TableSpec>(existing?.targets[0] ?? { ...emptySpec })
  const [columnMappings, setColumnMappings] = useState<ColumnMapping[]>(existing?.columnMappings ?? [])
  const [scripts, setScripts] = useState<ScriptBindings>(structuredClone(existing?.scripts ?? {}))
  const [provisioning, setProvisioning] = useState(
    existing?.provisioning ?? { createTargetTableIfMissing: false },
  )

  // What each side actually points at once the replication's endpoints are applied.
  const resolvedSource = resolveSide(task?.endpoints?.source ?? null, source)
  const resolvedTarget = resolveSide(task?.endpoints?.target ?? null, target)
  const sourceConnection = connections?.find((c) => c.name === resolvedSource.connectionName)

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

  const canSave = name
    && resolvedSource.connectionName && resolvedSource.database && source.table
    && resolvedTarget.connectionName && resolvedTarget.database && target.table
    && columnMappings.length > 0

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    await upsert.mutateAsync({
      mappingName: name,
      mapping: { name, sources: [source], targets: [target], columnMappings, scripts, provisioning },
    })
    onSaved(name)
  }

  return (
    <form onSubmit={save} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
      <div className="page-head">
        <h2 className="page-title mono">{existing ? existing.name : 'New table mapping'}</h2>
        {existing && <span className="badge badge-accent">MAPPED</span>}
        <div className="right">
          {existing && (
            <Link
              className="btn"
              to={`/replications/${encodeURIComponent(replicationName)}/mappings/${encodeURIComponent(existing.name)}/preview`}
              data-testid="preview-mapping-link"
            >
              Preview SQL
            </Link>
          )}
          {existing && (
            <Link
              className="btn"
              to={`/replications/${encodeURIComponent(replicationName)}/mappings/${encodeURIComponent(existing.name)}/verification`}
              data-testid="verify-mapping-link"
            >
              Verify
            </Link>
          )}
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
              <input className="input" required value={name} onChange={(e) => setName(e.target.value)} data-testid="mapping-name-input" />
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
            onChange={(v) => setSource({ ...v, filter: source.filter })}
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

      {/* Its own row beneath both sides rather than stacked under Source alone. It belongs to the
          source, but hanging it off one card made the two sides different heights and stopped them
          being comparable at a glance — which is the whole reason to put them next to each other. */}
      <div className="card">
        <div className="card-body">
          <Field label="Source filter — optional SQL predicate">
            {/* An editor rather than an input: a predicate that narrows a real table outgrows forty
                visible characters quickly, and this one is spliced into the reader's WHERE clause
                verbatim. */}
            <CodeEditor
              value={source.filter ?? ''}
              language="sql"
              onChange={(filter) => setSource({ ...source, filter: filter.trim() ? filter : null })}
              minLines={2}
              maxLines={8}
              testId="source-filter-editor"
            />
          </Field>
        </div>
      </div>

      <ColumnMappingEditor
        source={resolvedSource}
        target={resolvedTarget}
        mappings={columnMappings}
        onChange={setColumnMappings}
        targetExists={targetExists}
      />

      <ScriptBindingsCard
        bindings={scripts}
        // The mapping is the most specific level, so what it inherits is the replication's binding if
        // it has one and the source connection's otherwise — the same order the server resolves in.
        inherited={{ ...(sourceConnection?.scripts ?? {}), ...(task?.scripts ?? {}) }}
        level="mapping"
        onChange={setScripts}
      />

      {/* Only a saved mapping has a name the provisioning API can plan against. */}
      {existing && (
        <ProvisioningCard
          replicationName={replicationName}
          mappingName={existing.name}
          provisioning={provisioning}
          onChangeProvisioning={setProvisioning}
          stale={targetChangedSinceSave}
        />
      )}

      {!existing && targetExists === false && target.table && (
        <div className="card">
          <div className="card-body">
            <span className="hint" data-testid="provisioning-after-save-hint">
              <strong>{target.schema || 'dbo'}.{target.table}</strong> does not exist yet. Save the
              mapping and the Setup card below will show the <code>CREATE TABLE</code> it would run.
            </span>
          </div>
        </div>
      )}
    </form>
  )
}
