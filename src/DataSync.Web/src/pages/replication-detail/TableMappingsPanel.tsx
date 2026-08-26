import { useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { useDeleteTableMapping, useTableMapping, useTableMappings } from '../../api/hooks'
import { TableMappingForm } from './TableMappingForm'

function MappingRow({ replicationName, name, onEdit }: { replicationName: string; name: string; onEdit: () => void }) {
  const { data } = useTableMapping(replicationName, name)
  const del = useDeleteTableMapping(replicationName)

  return (
    <tr>
      <td>{name}</td>
      <td>{data ? `${data.sources[0]?.schema}.${data.sources[0]?.table}` : '…'}</td>
      <td>{data ? `${data.targets[0]?.schema}.${data.targets[0]?.table}` : '…'}</td>
      <td>{data?.columnMappings.length ?? '…'}</td>
      <td>
        <div className="row">
          <button className="btn btn-sm" onClick={onEdit}>
            Edit
          </button>
          <button className="btn btn-sm btn-danger" onClick={() => del.mutate(name)} data-testid={`delete-mapping-${name}`}>
            Delete
          </button>
        </div>
      </td>
    </tr>
  )
}

export function TableMappingsPanel({ replicationName }: { replicationName: string }) {
  const { data: names, error } = useTableMappings(replicationName)
  const [formState, setFormState] = useState<'closed' | 'new' | string>('closed')

  return (
    <div>
      <div className="card">
        <div className="row-between">
          <h2>Table Mappings</h2>
          {formState === 'closed' && (
            <button className="btn btn-primary" onClick={() => setFormState('new')} data-testid="new-mapping-button">
              New Mapping
            </button>
          )}
        </div>
        <ErrorBanner error={error} />
        {names && names.length === 0 && <p className="empty-state">No table mappings yet.</p>}
        {names && names.length > 0 && (
          <table data-testid="table-mappings-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Source</th>
                <th>Target</th>
                <th>Columns</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {names.map((n) => (
                <MappingRow key={n} replicationName={replicationName} name={n} onEdit={() => setFormState(n)} />
              ))}
            </tbody>
          </table>
        )}
      </div>

      {formState !== 'closed' && (
        <TableMappingFormLoader
          replicationName={replicationName}
          mappingName={formState === 'new' ? undefined : formState}
          onDone={() => setFormState('closed')}
          onCancel={() => setFormState('closed')}
        />
      )}
    </div>
  )
}

function TableMappingFormLoader({
  replicationName,
  mappingName,
  onDone,
  onCancel,
}: {
  replicationName: string
  mappingName?: string
  onDone: () => void
  onCancel: () => void
}) {
  const { data: existing, isLoading } = useTableMapping(replicationName, mappingName)
  if (mappingName && isLoading) return <div className="card">Loading…</div>
  return <TableMappingForm replicationName={replicationName} existing={existing} onDone={onDone} onCancel={onCancel} />
}
