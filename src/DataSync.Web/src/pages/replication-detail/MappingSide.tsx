import { Field } from '../../components/Field'
import { useConnections, useDatabases, useTables } from '../../api/hooks'
import type { EndpointRef, TableSpec } from '../../api/types'

interface Props {
  label: string
  /** The replication's endpoint for this side — what an un-overridden mapping uses. */
  inherited: EndpointRef | null
  spec: TableSpec
  onChange: (next: TableSpec) => void
  testIdPrefix: string
}

/**
 * One side of a table mapping: where it reads from or writes to, and which table.
 *
 * The endpoint is the replication's unless this mapping overrides it. Overriding is the design's own
 * toggle — off shows the inherited connection and database read-only, on turns them into pickers. The
 * table picker cascades from the *resolved* endpoint either way, so choosing a table works the same
 * whichever side of that toggle you are on.
 */
export function MappingSide({ label, inherited, spec, onChange, testIdPrefix }: Props) {
  const overriding = spec.connectionName !== null || spec.database !== null

  const connectionName = spec.connectionName ?? inherited?.connectionName ?? ''
  const database = spec.database ?? inherited?.database ?? ''

  const { data: connections } = useConnections()
  const { data: databases } = useDatabases(connectionName || undefined)
  const { data: tables } = useTables(connectionName || undefined, database || undefined)

  const selectedTableKey = spec.schema && spec.table ? `${spec.schema}.${spec.table}` : ''

  // Turning the override on starts from whatever is currently in effect, so it is a starting point
  // rather than a blank form; turning it off drops back to inheriting.
  const toggleOverride = () =>
    onChange(overriding
      ? { ...spec, connectionName: null, database: null }
      : { ...spec, connectionName, database })

  return (
    <div className="card" data-testid={`${testIdPrefix}-side`}>
      <div className="card-head tight">
        <span className="card-title sm">{label}</span>
        {!overriding && <span className="badge">INHERITED</span>}
        <span className="spacer row" style={{ gap: 7 }}>
          <button
            type="button"
            className={`toggle ${overriding ? 'on' : ''}`}
            onClick={toggleOverride}
            aria-pressed={overriding}
            data-testid={`${testIdPrefix}-override-toggle`}
          />
          <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>Override for this table</span>
        </span>
      </div>

      <div className="card-body">
        <div className="form-grid">
          <Field label="Connection">
            {overriding ? (
              <select
                className="select"
                value={connectionName}
                onChange={(e) => onChange({ ...spec, connectionName: e.target.value, database: '', schema: '', table: '' })}
                data-testid={`${testIdPrefix}-connection-select`}
              >
                <option value="">Select…</option>
                {connections?.map((c) => <option key={c.name} value={c.name}>{c.name}</option>)}
              </select>
            ) : (
              <span className="input" style={{ display: 'flex', alignItems: 'center', background: 'var(--sunken)', borderColor: '#f0eee8', color: 'var(--ink-3)' }}>
                {connectionName || '—'}
              </span>
            )}
          </Field>

          <Field label="Database">
            {overriding ? (
              <select
                className="select"
                value={database}
                disabled={!connectionName}
                onChange={(e) => onChange({ ...spec, database: e.target.value, schema: '', table: '' })}
                data-testid={`${testIdPrefix}-database-select`}
              >
                <option value="">Select…</option>
                {databases?.map((d) => <option key={d} value={d}>{d}</option>)}
              </select>
            ) : (
              <span className="input" style={{ display: 'flex', alignItems: 'center', background: 'var(--sunken)', borderColor: '#f0eee8', color: 'var(--ink-3)' }}>
                {database || '—'}
              </span>
            )}
          </Field>
        </div>

        <Field label="Table">
          <select
            className="select"
            value={selectedTableKey}
            disabled={!database}
            onChange={(e) => {
              const [schema, table] = e.target.value.split('.')
              onChange({ ...spec, schema: schema ?? '', table: table ?? '' })
            }}
            data-testid={`${testIdPrefix}-table-select`}
          >
            <option value="">Select…</option>
            {tables?.map((t) => (
              <option key={`${t.schema}.${t.table}`} value={`${t.schema}.${t.table}`}>{t.schema}.{t.table}</option>
            ))}
          </select>
        </Field>
      </div>
    </div>
  )
}
