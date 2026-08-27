import { Field } from '../../components/Field'
import { useConnections, useDatabases, useTables } from '../../api/hooks'
import type { TableRef } from '../../api/types'

interface Props {
  value: TableRef
  onChange: (next: TableRef) => void
  testIdPrefix: string
}

/** Cascading connection → database → table picker, backed by the metadata-browsing endpoints, so a
 * table name never has to be typed. */
export function TableSidePicker({ value, onChange, testIdPrefix }: Props) {
  const { data: connections } = useConnections()
  const { data: databases } = useDatabases(value.connectionName || undefined)
  const { data: tables } = useTables(value.connectionName || undefined, value.database || undefined)

  const selectedTableKey = value.schema && value.table ? `${value.schema}.${value.table}` : ''

  return (
    <>
      <div className="form-grid">
        <Field label="Connection">
          <select
            className="select"
            value={value.connectionName}
            onChange={(e) => onChange({ connectionName: e.target.value, database: '', schema: '', table: '' })}
            data-testid={`${testIdPrefix}-connection-select`}
          >
            <option value="">Select…</option>
            {connections?.map((c) => <option key={c.name} value={c.name}>{c.name}</option>)}
          </select>
        </Field>
        <Field label="Database">
          <select
            className="select"
            value={value.database}
            disabled={!value.connectionName}
            onChange={(e) => onChange({ ...value, database: e.target.value, schema: '', table: '' })}
            data-testid={`${testIdPrefix}-database-select`}
          >
            <option value="">Select…</option>
            {databases?.map((d) => <option key={d} value={d}>{d}</option>)}
          </select>
        </Field>
      </div>
      <Field label="Table">
        <select
          className="select"
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
            <option key={`${t.schema}.${t.table}`} value={`${t.schema}.${t.table}`}>{t.schema}.{t.table}</option>
          ))}
        </select>
      </Field>
    </>
  )
}
