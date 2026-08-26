import { useEffect, useState } from 'react'
import { useColumns } from '../../api/hooks'
import type { ColumnMapping, TableRef } from '../../api/types'

interface Props {
  source: TableRef
  target: TableRef
  mappings: ColumnMapping[]
  onChange: (mappings: ColumnMapping[]) => void
}

/** Once both sides of a mapping have a table selected, lists the target's columns (source of truth
 * for what needs to be populated) and lets the user pick which source column feeds each one —
 * auto-suggesting a same-name match on first load. */
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
    // Only auto-suggest once, when both column lists first become available and nothing is mapped yet.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sourceColumns, targetColumns])

  if (!sourceColumns || !targetColumns) {
    return <p className="muted">Select a source and target table to map columns.</p>
  }

  const mappedTargets = new Set(mappings.map((m) => m.targetColumn))
  const unmappedTargets = targetColumns.filter((tc) => !mappedTargets.has(tc.name))

  const updateRow = (index: number, patch: Partial<ColumnMapping>) => {
    const next = [...mappings]
    next[index] = { ...next[index], ...patch }
    onChange(next)
  }

  const removeRow = (index: number) => onChange(mappings.filter((_, i) => i !== index))

  const addRow = (targetColumn: string) => {
    const suggestion = sourceColumns.find((c) => c.name === targetColumn)?.name ?? sourceColumns[0]?.name ?? ''
    onChange([...mappings, { sourceColumn: suggestion, targetColumn, transform: null }])
  }

  return (
    <div className="stack">
      <table data-testid="column-mappings-table">
        <thead>
          <tr>
            <th>Source Column</th>
            <th>Target Column</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {mappings.map((m, i) => (
            <tr key={i}>
              <td>
                <select
                  value={m.sourceColumn}
                  onChange={(e) => updateRow(i, { sourceColumn: e.target.value })}
                  data-testid={`column-mapping-source-${i}`}
                >
                  {sourceColumns.map((c) => (
                    <option key={c.name} value={c.name}>
                      {c.name} ({c.nativeType})
                    </option>
                  ))}
                </select>
              </td>
              <td>
                {m.targetColumn}
                {targetColumns.find((c) => c.name === m.targetColumn)?.isPrimaryKey && (
                  <span className="badge badge-neutral" style={{ marginLeft: 6 }}>
                    PK
                  </span>
                )}
              </td>
              <td>
                <button type="button" className="btn btn-sm btn-danger" onClick={() => removeRow(i)}>
                  Remove
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {unmappedTargets.length > 0 && (
        <div className="row">
          <select value={columnToAdd} onChange={(e) => setColumnToAdd(e.target.value)} data-testid="add-target-column-select">
            <option value="" disabled>
              Add target column…
            </option>
            {unmappedTargets.map((c) => (
              <option key={c.name} value={c.name}>
                {c.name} {c.isPrimaryKey ? '(PK)' : ''}
              </option>
            ))}
          </select>
          <button
            type="button"
            className="btn btn-sm"
            data-testid="add-target-column-button"
            onClick={() => {
              if (!columnToAdd) return
              addRow(columnToAdd)
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
