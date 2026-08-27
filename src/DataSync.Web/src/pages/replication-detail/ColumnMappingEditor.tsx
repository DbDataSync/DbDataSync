import { useEffect, useState } from 'react'
import { useColumns } from '../../api/hooks'
import type { ColumnMapping, ResolvedRef } from '../../api/types'

interface Props {
  source: ResolvedRef
  target: ResolvedRef
  mappings: ColumnMapping[]
  onChange: (mappings: ColumnMapping[]) => void
}

const COLUMNS = '1fr 26px 1fr 110px 80px'

/**
 * Once both sides have a table, lists the target's columns and lets each be fed from a source column,
 * auto-suggesting a same-name match on first load. The design adds the source column's SQL type
 * beside its name and a PK badge on the target — both come from the metadata endpoint, so both are
 * real; the mockup's per-row Transform value is a field on ColumnMapping and is editable here.
 */
export function ColumnMappingEditor({ source, target, mappings, onChange }: Props) {
  const { data: sourceColumns } = useColumns(source.connectionName, source.database, source.schema, source.table)
  const { data: targetColumns } = useColumns(target.connectionName, target.database, target.schema, target.table)
  const [columnToAdd, setColumnToAdd] = useState('')

  useEffect(() => {
    if (!targetColumns || !sourceColumns || mappings.length > 0) return
    const sourceNames = new Set(sourceColumns.map((c) => c.name))
    const suggested = targetColumns
      .filter((tc) => sourceNames.has(tc.name))
      .map((tc) => ({ sourceColumn: tc.name, targetColumn: tc.name, transform: null }))
    if (suggested.length > 0) onChange(suggested)
    // Only auto-suggest once, when both column lists first become available and nothing is mapped.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sourceColumns, targetColumns])

  if (!sourceColumns || !targetColumns) {
    return (
      <div className="card">
        <div className="card-head tight"><span className="card-title sm">Column mappings</span></div>
        <div className="empty">Select a source and target table to map columns.</div>
      </div>
    )
  }

  const typeOf = (name: string) => sourceColumns.find((c) => c.name === name)?.nativeType
  const isPk = (name: string) => targetColumns.find((c) => c.name === name)?.isPrimaryKey
  const mapped = new Set(mappings.map((m) => m.targetColumn))
  const unmapped = targetColumns.filter((tc) => !mapped.has(tc.name))

  const updateRow = (index: number, patch: Partial<ColumnMapping>) =>
    onChange(mappings.map((m, i) => (i === index ? { ...m, ...patch } : m)))

  const autoMap = () => {
    const sourceNames = new Set(sourceColumns.map((c) => c.name))
    onChange(targetColumns.filter((tc) => sourceNames.has(tc.name))
      .map((tc) => ({ sourceColumn: tc.name, targetColumn: tc.name, transform: null })))
  }

  return (
    <div className="card flush" data-testid="column-mappings-table">
      <div className="card-head tight">
        <span className="card-title sm">Column mappings</span>
        <span className="card-note">{mappings.length} of {targetColumns.length} target columns mapped</span>
        <button type="button" className="btn btn-sm spacer" onClick={autoMap}>Auto-map by name</button>
      </div>

      <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 0, height: 29 }}>
        <span>Source column</span><span /><span>Target column</span><span>Transform</span><span />
      </div>

      {mappings.map((m, i) => (
        <div key={m.targetColumn} className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 0 }}>
          <span className="row" style={{ gap: 7 }}>
            <select
              className="select sm"
              style={{ maxWidth: 190 }}
              value={m.sourceColumn}
              onChange={(e) => updateRow(i, { sourceColumn: e.target.value })}
              data-testid={`column-mapping-source-${i}`}
            >
              {sourceColumns.map((c) => <option key={c.name} value={c.name}>{c.name}</option>)}
            </select>
            <span className="faint">{typeOf(m.sourceColumn)}</span>
          </span>
          <span className="faint">→</span>
          <span className="row" style={{ gap: 7 }}>
            {m.targetColumn}
            {isPk(m.targetColumn) && <span className="badge badge-accent">PK</span>}
          </span>
          <span>
            <input
              className="input sm"
              style={{ maxWidth: 100 }}
              placeholder="—"
              value={m.transform ?? ''}
              onChange={(e) => updateRow(i, { transform: e.target.value || null })}
              aria-label={`${m.targetColumn} transform`}
            />
          </span>
          <button
            type="button"
            className="btn-link quiet"
            style={{ justifySelf: 'end' }}
            onClick={() => onChange(mappings.filter((_, index) => index !== i))}
          >
            Remove
          </button>
        </div>
      ))}

      {mappings.length === 0 && <div className="empty">No columns mapped yet.</div>}

      {unmapped.length > 0 && (
        <div className="row" style={{ height: 38, padding: '0 14px', gap: 8, borderTop: '1px solid var(--row-edge)' }}>
          <select
            className="select sm"
            style={{ maxWidth: 240 }}
            value={columnToAdd}
            onChange={(e) => setColumnToAdd(e.target.value)}
            data-testid="add-target-column-select"
          >
            <option value="">Add target column…</option>
            {unmapped.map((c) => <option key={c.name} value={c.name}>{c.name}{c.isPrimaryKey ? ' (PK)' : ''}</option>)}
          </select>
          <button
            type="button"
            className="btn btn-sm"
            data-testid="add-target-column-button"
            disabled={!columnToAdd}
            onClick={() => {
              if (!columnToAdd) return
              const suggestion = sourceColumns.find((c) => c.name === columnToAdd)?.name ?? sourceColumns[0]?.name ?? ''
              onChange([...mappings, { sourceColumn: suggestion, targetColumn: columnToAdd, transform: null }])
              setColumnToAdd('')
            }}
          >
            Add
          </button>
        </div>
      )}
    </div>
  )
}
