import { useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Field } from '../../components/Field'
import { useDeleteTableMapping, useReplication, useUpsertTableMapping } from '../../api/hooks'
import type { ColumnMapping, SourceTableSpec, TableMappingConfig, TableSpec } from '../../api/types'
import { MappingSide } from './MappingSide'
import { resolveSide } from '../../api/resolveEndpoint'
import { ColumnMappingEditor } from './ColumnMappingEditor'

/** A new mapping inherits both endpoints — null connection and database — and states only its table. */
const emptySpec: TableSpec = { connectionName: null, database: null, schema: '', table: '' }

interface Props {
  replicationName: string
  existing?: TableMappingConfig
  onDone: () => void
  onCancel: () => void
}

export function TableMappingForm({ replicationName, existing, onDone, onCancel }: Props) {
  const upsert = useUpsertTableMapping(replicationName)
  const del = useDeleteTableMapping(replicationName)
  const { data: task } = useReplication(replicationName)
  const [name, setName] = useState(existing?.name ?? '')
  const [source, setSource] = useState<SourceTableSpec>(existing?.sources[0] ?? { ...emptySpec, filter: null })
  const [target, setTarget] = useState<TableSpec>(existing?.targets[0] ?? { ...emptySpec })
  const [columnMappings, setColumnMappings] = useState<ColumnMapping[]>(existing?.columnMappings ?? [])

  // What each side actually points at once the replication's endpoints are applied.
  const resolvedSource = resolveSide(task?.endpoints?.source ?? null, source)
  const resolvedTarget = resolveSide(task?.endpoints?.target ?? null, target)

  const canSave = name
    && resolvedSource.connectionName && resolvedSource.database && source.table
    && resolvedTarget.connectionName && resolvedTarget.database && target.table
    && columnMappings.length > 0

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    await upsert.mutateAsync({
      mappingName: name,
      mapping: { name, sources: [source], targets: [target], columnMappings },
    })
    onDone()
  }

  return (
    <form onSubmit={save} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
      <div className="page-head">
        <h2 className="page-title mono">{existing ? existing.name : 'New table mapping'}</h2>
        {existing && <span className="badge badge-accent">MAPPED</span>}
        <div className="right">
          <button type="button" className="btn" onClick={onCancel}>Cancel</button>
          {existing && (
            <button
              type="button"
              className="btn btn-danger"
              onClick={async () => { await del.mutateAsync(existing.name); onDone() }}
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

      <div className="form-grid">
        <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <MappingSide
            label="Source"
            inherited={task?.endpoints?.source ?? null}
            spec={source}
            onChange={(v) => setSource({ ...v, filter: source.filter })}
            testIdPrefix="source"
          />
          <div className="card">
            <div className="card-body">
              <Field label="Source filter — optional SQL predicate">
                <input
                  className="input"
                  placeholder="e.g. Status = 'Active'"
                  value={source.filter ?? ''}
                  onChange={(e) => setSource({ ...source, filter: e.target.value || null })}
                />
              </Field>
            </div>
          </div>
        </div>

        <MappingSide
          label="Target"
          inherited={task?.endpoints?.target ?? null}
          spec={target}
          onChange={setTarget}
          testIdPrefix="target"
        />
      </div>

      <ColumnMappingEditor
        source={resolvedSource}
        target={resolvedTarget}
        mappings={columnMappings}
        onChange={setColumnMappings}
      />
    </form>
  )
}
