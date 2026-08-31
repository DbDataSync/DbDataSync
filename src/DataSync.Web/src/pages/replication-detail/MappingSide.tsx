import { Field } from '../../components/Field'
import { EndpointSideCard } from '../../components/EndpointSidePair'
import { useConnections, useDatabases, useTables } from '../../api/hooks'
import { tableExists } from '../../api/tableExists'
import type { EndpointRef, TableSpec } from '../../api/types'

interface Props {
  /** Which side this is: the card's accent colour and its title come from it. */
  side: 'source' | 'target'
  label: string
  /** The replication's endpoint for this side — what an un-overridden mapping uses. */
  inherited: EndpointRef | null
  spec: TableSpec
  onChange: (next: TableSpec) => void
  testIdPrefix: string
  /**
   * Target side only. Lets the operator name a table the database does not have yet, so provisioning
   * can create it — the reason this control exists at all.
   *
   * Not offered on the source, where a name that is not in the catalog is a typo rather than an
   * intention: there is no such thing as creating a source table to read from.
   */
  allowNewTable?: boolean
}

/**
 * One side of a table mapping: where it reads from or writes to, and which table.
 *
 * The endpoint is the replication's unless this mapping overrides it. Overriding is the design's own
 * toggle — off shows the inherited connection and database read-only, on turns them into pickers. The
 * table picker cascades from the *resolved* endpoint either way, so choosing a table works the same
 * whichever side of that toggle you are on.
 */
export function MappingSide({ side, label, inherited, spec, onChange, testIdPrefix, allowNewTable = false }: Props) {
  const overriding = spec.connectionName !== null || spec.database !== null

  const connectionName = spec.connectionName ?? inherited?.connectionName ?? ''
  const database = spec.database ?? inherited?.database ?? ''

  const { data: connections } = useConnections()
  const { data: databases } = useDatabases(connectionName || undefined)
  const { data: tables } = useTables(connectionName || undefined, database || undefined)

  // The selection is an **index** into the loaded list, never a "schema.table" string. Combining the
  // two and splitting them back apart is fine until a schema or table name contains a literal '.',
  // which a quoted identifier allows — and then the split silently produces the wrong pair.
  const selectedTableIndex = (tables ?? [])
    .findIndex((t) => t.schema === spec.schema && t.table === spec.table)
  const exists = tableExists(tables, spec.schema, spec.table)

  const schemas = [...new Set((tables ?? []).map((t) => t.schema))]
  const tablesInSchema = (tables ?? []).filter((t) => t.schema === spec.schema)

  /**
   * Picking a name out of the suggestions brings its schema with it, so choosing `Orders` from a list
   * that shows it under `sales` does not silently leave the schema box pointing somewhere else.
   * Only when the name is unambiguous — two schemas holding a table of the same name is exactly when
   * guessing would be wrong.
   */
  const setTableName = (table: string) => {
    const matches = (tables ?? []).filter((t) => t.table === table)
    const schema = matches.length === 1 ? matches[0].schema : spec.schema
    onChange({ ...spec, schema, table })
  }

  // Turning the override on starts from whatever is currently in effect, so it is a starting point
  // rather than a blank form; turning it off drops back to inheriting.
  const toggleOverride = () =>
    onChange(overriding
      ? { ...spec, connectionName: null, database: null }
      : { ...spec, connectionName, database })

  return (
    <EndpointSideCard
      side={side}
      title={label}
      testId={`${testIdPrefix}-side`}
      head={
        <>
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
        </>
      }
    >
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

        {allowNewTable ? (
          <>
            {/* Schema and table stay two fields rather than one `dbo.Orders` box. Parsing that back
                out is a guess about quoting, and it is wrong the moment a name contains a dot. */}
            <div className="form-grid">
              <Field label="Schema">
                <input
                  className="input"
                  list={`${testIdPrefix}-schema-options`}
                  value={spec.schema}
                  disabled={!database}
                  placeholder="dbo"
                  onChange={(e) => onChange({ ...spec, schema: e.target.value })}
                  data-testid={`${testIdPrefix}-schema-input`}
                />
                <datalist id={`${testIdPrefix}-schema-options`}>
                  {schemas.map((s) => <option key={s} value={s} />)}
                </datalist>
              </Field>

              <Field label="Table">
                <input
                  className="input"
                  list={`${testIdPrefix}-table-options`}
                  value={spec.table}
                  disabled={!database}
                  onChange={(e) => setTableName(e.target.value)}
                  data-testid={`${testIdPrefix}-table-input`}
                />
                <datalist id={`${testIdPrefix}-table-options`}>
                  {tablesInSchema.map((t) => <option key={t.table} value={t.table} />)}
                </datalist>
              </Field>
            </div>

            {exists === false && (
              <div className="hint" data-testid={`${testIdPrefix}-table-will-be-created`}>
                <span className="badge badge-accent">NEW</span>{' '}
                does not exist yet — save the mapping and apply the plan in Provisioning to create it
              </div>
            )}
          </>
        ) : (
          <Field label="Table">
            <select
              className="select"
              value={selectedTableIndex < 0 ? '' : String(selectedTableIndex)}
              disabled={!database}
              onChange={(e) => {
                const picked = (tables ?? [])[Number(e.target.value)]
                onChange(picked
                  ? { ...spec, schema: picked.schema, table: picked.table }
                  : { ...spec, schema: '', table: '' })
              }}
              data-testid={`${testIdPrefix}-table-select`}
            >
              <option value="">Select…</option>
              {tables?.map((t, i) => (
                // The label still reads "schema.table" — combining is fine for something a person
                // reads. It is only a bug when the combined string becomes data somebody parses.
                <option key={`${i}`} value={`${i}`}>{t.schema}.{t.table}</option>
              ))}
            </select>
          </Field>
        )}
    </EndpointSideCard>
  )
}
