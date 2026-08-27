import { useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Field } from '../../components/Field'
import { useDeleteTableMapping, useUpsertTableMapping } from '../../api/hooks'
import type { ColumnMapping, SourceTableRef, TableMappingConfig, TableRef } from '../../api/types'
import { TableSidePicker } from './TableSidePicker'
import { ColumnMappingEditor } from './ColumnMappingEditor'

const emptyRef: TableRef = { connectionName: '', database: '', schema: '', table: '' }

interface Props {
  replicationName: string
  existing?: TableMappingConfig
  onDone: () => void
  onCancel: () => void
}

export function TableMappingForm({ replicationName, existing, onDone, onCancel }: Props) {
  const upsert = useUpsertTableMapping(replicationName)
  const del = useDeleteTableMapping(replicationName)
  const [name, setName] = useState(existing?.name ?? '')
  const [source, setSource] = useState<SourceTableRef>(existing?.sources[0] ?? { ...emptyRef, filter: null })
  const [target, setTarget] = useState<TableRef>(existing?.targets[0] ?? { ...emptyRef })
  const [columnMappings, setColumnMappings] = useState<ColumnMapping[]>(existing?.columnMappings ?? [])

  const canSave = name && source.connectionName && source.table && target.connectionName && target.table && columnMappings.length > 0

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
        <div className="card">
          <div className="card-head tight">
            <span className="card-title sm">Source</span>
            {source.connectionName && <span className="mono" style={{ font: '400 11px var(--mono)', color: 'var(--ink-10)' }}>{source.connectionName}</span>}
          </div>
          <div className="card-body">
            <TableSidePicker value={source} onChange={(v) => setSource({ ...v, filter: source.filter })} testIdPrefix="source" />
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

        <div className="card">
          <div className="card-head tight">
            <span className="card-title sm">Target</span>
            {target.connectionName && <span className="mono" style={{ font: '400 11px var(--mono)', color: 'var(--ink-10)' }}>{target.connectionName}</span>}
          </div>
          <div className="card-body">
            <TableSidePicker value={target} onChange={setTarget} testIdPrefix="target" />
          </div>
        </div>
      </div>

      <ColumnMappingEditor source={source} target={target} mappings={columnMappings} onChange={setColumnMappings} />
    </form>
  )
}
