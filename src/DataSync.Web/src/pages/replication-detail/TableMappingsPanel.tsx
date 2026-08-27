import { useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { useTableMapping, useTableMappings } from '../../api/hooks'
import { TableMappingForm } from './TableMappingForm'

/**
 * The design replaces the list-then-form stack with a mappings sidebar and the selected mapping's
 * editor filling the pane — the editor is the screen, not a card appended below a table.
 */
export function TableMappingsPanel({ replicationName }: { replicationName: string }) {
  const { data: names, error } = useTableMappings(replicationName)
  const [selected, setSelected] = useState<string | 'new' | null>(null)

  const active = selected ?? names?.[0] ?? null

  return (
    <>
      <aside className="sidebar">
        <div className="sidebar-head">
          <span>Mappings</span>
          <button className="btn-link" onClick={() => setSelected('new')} data-testid="new-mapping-button" title="New mapping">+</button>
        </div>
        <div className="sidebar-list">
          {(names ?? []).map((n) => (
            <MappingSidebarItem
              key={n}
              replicationName={replicationName}
              name={n}
              active={active === n}
              onSelect={() => setSelected(n)}
            />
          ))}
          {names?.length === 0 && <span className="hint" style={{ padding: '6px 7px' }}>No mappings yet.</span>}
          {active === 'new' && <span className="sidebar-item active">new mapping</span>}
        </div>
      </aside>

      <div className="pane">
        <ErrorBanner error={error} />
        {active === null && <div className="empty">Select a mapping, or add one.</div>}
        {active !== null && (
          <MappingEditor
            // Remounts on selection so the form's own draft state starts from the right mapping.
            key={active}
            replicationName={replicationName}
            mappingName={active === 'new' ? undefined : active}
            onDone={() => setSelected(null)}
          />
        )}
      </div>
    </>
  )
}

function MappingSidebarItem({ replicationName, name, active, onSelect }: {
  replicationName: string
  name: string
  active: boolean
  onSelect: () => void
}) {
  const { data } = useTableMapping(replicationName, name)
  return (
    <button className={`sidebar-item ${active ? 'active' : ''}`} onClick={onSelect} data-testid={`mapping-item-${name}`}>
      {name}
      <span className="meta">{data ? `${data.columnMappings.length} cols` : '…'}</span>
    </button>
  )
}

function MappingEditor({ replicationName, mappingName, onDone }: {
  replicationName: string
  mappingName?: string
  onDone: () => void
}) {
  const { data: existing, isLoading } = useTableMapping(replicationName, mappingName)
  if (mappingName && isLoading) return <div className="empty">Loading…</div>
  return <TableMappingForm replicationName={replicationName} existing={existing} onDone={onDone} onCancel={onDone} />
}
