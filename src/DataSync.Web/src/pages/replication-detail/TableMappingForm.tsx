import { useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { useUpsertTableMapping } from '../../api/hooks'
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
  const [name, setName] = useState(existing?.name ?? '')
  const [source, setSource] = useState<SourceTableRef>(existing?.sources[0] ?? { ...emptyRef, filter: null })
  const [target, setTarget] = useState<TableRef>(existing?.targets[0] ?? { ...emptyRef })
  const [columnMappings, setColumnMappings] = useState<ColumnMapping[]>(existing?.columnMappings ?? [])

  const canSave = name && source.connectionName && source.table && target.connectionName && target.table && columnMappings.length > 0

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    const mapping: TableMappingConfig = {
      name,
      sources: [source],
      targets: [target],
      columnMappings,
    }
    await upsert.mutateAsync({ mappingName: name, mapping })
    onDone()
  }

  return (
    <div className="card">
      <h2>{existing ? `Edit Mapping: ${existing.name}` : 'New Table Mapping'}</h2>
      <ErrorBanner error={upsert.error} />
      <form onSubmit={save} className="stack">
        <div className="form-field">
          <label htmlFor="mapping-name">Mapping Name</label>
          <input
            id="mapping-name"
            required
            disabled={!!existing}
            value={name}
            onChange={(e) => setName(e.target.value)}
            data-testid="mapping-name-input"
          />
        </div>

        <TableSidePicker label="Source" value={source} onChange={(v) => setSource({ ...v, filter: source.filter })} testIdPrefix="source" />
        <div className="form-field">
          <label htmlFor="source-filter">Source Filter (optional SQL predicate)</label>
          <input
            id="source-filter"
            placeholder="e.g. Status = 'Active'"
            value={source.filter ?? ''}
            onChange={(e) => setSource({ ...source, filter: e.target.value || null })}
          />
        </div>

        <TableSidePicker label="Target" value={target} onChange={setTarget} testIdPrefix="target" />

        <div>
          <strong>Column Mappings</strong>
          <ColumnMappingEditor source={source} target={target} mappings={columnMappings} onChange={setColumnMappings} />
        </div>

        <div className="form-actions">
          <button type="submit" className="btn btn-primary" disabled={!canSave || upsert.isPending} data-testid="save-mapping-button">
            {upsert.isPending ? 'Saving…' : 'Save Mapping'}
          </button>
          <button type="button" className="btn" onClick={onCancel}>
            Cancel
          </button>
        </div>
      </form>
    </div>
  )
}
