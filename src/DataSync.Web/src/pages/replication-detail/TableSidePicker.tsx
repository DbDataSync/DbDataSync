import { useConnections, useDatabases, useTables } from '../../api/hooks'
import type { TableRef } from '../../api/types'

interface Props {
  label: string
  value: TableRef
  onChange: (next: TableRef) => void
  testIdPrefix: string
}

/** Cascading connection -> database -> table picker, backed by the metadata browsing endpoints
 * (architecture/detailed-design.md §3.1) — used for both the source and target side of a table
 * mapping so the user never has to hand-type a table name. */
export function TableSidePicker({ label, value, onChange, testIdPrefix }: Props) {
  const { data: connections } = useConnections()
  const { data: databases } = useDatabases(value.connectionName || undefined)
  const { data: tables } = useTables(value.connectionName || undefined, value.database || undefined)

  const selectedTableKey = value.schema && value.table ? `${value.schema}.${value.table}` : ''

  return (
    <div className="stack">
      <strong>{label}</strong>
      <div className="form-grid">
        <div className="form-field">
          <label>Connection</label>
          <select
            value={value.connectionName}
            onChange={(e) => onChange({ connectionName: e.target.value, database: '', schema: '', table: '' })}
            data-testid={`${testIdPrefix}-connection-select`}
          >
            <option value="">Select…</option>
            {connections?.map((c) => (
              <option key={c.name} value={c.name}>
                {c.name}
              </option>
            ))}
          </select>
        </div>
        <div className="form-field">
          <label>Database</label>
          <select
            value={value.database}
            disabled={!value.connectionName}
            onChange={(e) => onChange({ ...value, database: e.target.value, schema: '', table: '' })}
            data-testid={`${testIdPrefix}-database-select`}
          >
            <option value="">Select…</option>
            {databases?.map((d) => (
              <option key={d} value={d}>
                {d}
              </option>
            ))}
          </select>
        </div>
        <div className="form-field span-2">
          <label>Table</label>
          <select
            value={selectedTableKey}
            disabled={!value.database}
            onChange={(e) => {
              const [schema, table] = e.target.value.split('.')
              onChange({ ...value, schema: schema ?? '', table: table ?? '' })
            }}
            data-testid={`${testIdPrefix}-table-select`}
          >
            <option value="">Select…</option>
            {tables?.map((t) => (
              <option key={`${t.schema}.${t.table}`} value={`${t.schema}.${t.table}`}>
                {t.schema}.{t.table}
              </option>
            ))}
          </select>
        </div>
      </div>
    </div>
  )
}
