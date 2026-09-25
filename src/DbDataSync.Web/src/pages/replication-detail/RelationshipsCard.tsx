import { Field } from '../../components/Field'
import { useColumns, useTables } from '../../api/hooks'
import type { ColumnMetadata, RelationshipConfig, RelationshipJoinKey, ResolvedRef } from '../../api/types'

/**
 * Named joins from this mapping's own primary source table to a foreign table on the same connection
 * and database — phase 186J/189J. Each relationship's foreign table is picked from the same catalog the
 * primary source's own table picker already reads (185J/186J's own "same connection, same database"
 * constraint), and its columns are fetched live the same way the primary source's and target's already
 * are — there is no separate "browse and preview" flow to build, because a relationship's foreign side
 * is always a real, catalog-introspectable table, never a query.
 *
 * A self-join — picking the mapping's own primary schema/table as a relationship's foreign side — needs
 * no special handling here: it is just an ordinary pick from the same list the primary table is in.
 */
export function RelationshipsCard({ resolvedSource, sourceColumns, relationships, onChange }: {
  resolvedSource: ResolvedRef
  /** The primary table's own columns, for a join key's "local column" picker. Undefined while the
   * catalog is still loading, or for a mapping with no source table chosen yet. */
  sourceColumns: ColumnMetadata[] | undefined
  relationships: RelationshipConfig[]
  onChange: (next: RelationshipConfig[]) => void
}) {
  const { data: tables } = useTables(
    resolvedSource.connectionName || undefined, resolvedSource.database || undefined)

  const replace = (index: number, next: RelationshipConfig) =>
    onChange(relationships.map((r, i) => (i === index ? next : r)))

  const remove = (index: number) => onChange(relationships.filter((_, i) => i !== index))

  const addRelationship = () => {
    const existingNames = new Set(relationships.map((r) => r.name))
    let n = relationships.length + 1
    while (existingNames.has(`relationship${n}`)) n++
    onChange([
      ...relationships,
      { name: `relationship${n}`, schema: '', table: '', joinKeys: [{ localColumn: '', foreignColumn: '' }] },
    ])
  }

  return (
    <div className="card flush" data-testid="relationships-card">
      <div className="card-head tight">
        <span className="card-title sm">Relationships</span>
        <span className="card-note">
          named joins to other tables on the same connection, for a column mapping to pull a value
          through
        </span>
        <button type="button" className="btn btn-sm spacer" onClick={addRelationship} data-testid="add-relationship-button">
          Add relationship
        </button>
      </div>

      {relationships.length === 0 && <div className="empty">No relationships declared.</div>}

      {relationships.map((relationship, i) => (
        <RelationshipRow
          key={i}
          resolvedSource={resolvedSource}
          sourceColumns={sourceColumns}
          tables={tables ?? []}
          relationship={relationship}
          onChange={(next) => replace(i, next)}
          onRemove={() => remove(i)}
          testIndex={i}
        />
      ))}
    </div>
  )
}

function RelationshipRow({ resolvedSource, sourceColumns, tables, relationship, onChange, onRemove, testIndex }: {
  resolvedSource: ResolvedRef
  sourceColumns: ColumnMetadata[] | undefined
  tables: { schema: string; table: string }[]
  relationship: RelationshipConfig
  onChange: (next: RelationshipConfig) => void
  onRemove: () => void
  testIndex: number
}) {
  // Each row fetches its own foreign table's columns — encapsulated here, one hook call per relationship,
  // rather than in the list above, which cannot call a hook a variable number of times per render.
  const { data: foreignColumns } = useColumns(
    resolvedSource.connectionName || undefined, resolvedSource.database || undefined,
    relationship.schema || undefined, relationship.table || undefined)

  const selectedTableIndex = tables.findIndex(
    (t) => t.schema === relationship.schema && t.table === relationship.table)

  const setTable = (index: number) => {
    const picked = tables[index]
    onChange(picked ? { ...relationship, schema: picked.schema, table: picked.table } : { ...relationship, schema: '', table: '' })
  }

  const replaceJoinKey = (index: number, next: RelationshipJoinKey) =>
    onChange({ ...relationship, joinKeys: relationship.joinKeys.map((k, i) => (i === index ? next : k)) })

  const removeJoinKey = (index: number) =>
    onChange({ ...relationship, joinKeys: relationship.joinKeys.filter((_, i) => i !== index) })

  const addJoinKey = () =>
    onChange({ ...relationship, joinKeys: [...relationship.joinKeys, { localColumn: '', foreignColumn: '' }] })

  return (
    // .grid-row's own fixed 38px height is load-bearing for the dense tabular rows most of this app
    // uses it for (see index.css's own comment) — wrong for this row, which stacks a name/table line
    // and a join-keys line and genuinely grows. `.auto`, the same variant the Monitoring screen's own
    // multi-line lag cell already uses for the identical reason, instead of a fixed height that clips
    // the second line rather than showing it.
    <div className="grid-row auto" style={{ display: 'flex', flexDirection: 'column', gap: 8, padding: '10px 14px' }}>
      <div className="row" style={{ gap: 8, alignItems: 'flex-end' }}>
        <Field label="Name">
          <input
            className="input sm"
            style={{ maxWidth: 160 }}
            value={relationship.name}
            onChange={(e) => onChange({ ...relationship, name: e.target.value })}
            data-testid={`relationship-name-${testIndex}`}
          />
        </Field>
        <Field label="Foreign table">
          <select
            className="select sm"
            value={selectedTableIndex < 0 ? '' : String(selectedTableIndex)}
            onChange={(e) => setTable(Number(e.target.value))}
            data-testid={`relationship-table-${testIndex}`}
          >
            <option value="">Select…</option>
            {tables.map((t, i) => <option key={i} value={i}>{t.schema}.{t.table}</option>)}
          </select>
        </Field>
        <button
          type="button"
          className="btn-link quiet spacer"
          style={{ justifySelf: 'end' }}
          onClick={onRemove}
          data-testid={`remove-relationship-${testIndex}`}
        >
          Remove
        </button>
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <span className="hint">Join keys — ANDed together</span>
        {relationship.joinKeys.map((key, ki) => (
          <div key={ki} className="row" style={{ gap: 8, alignItems: 'flex-end' }}>
            <Field label="Local column">
              <select
                className="select sm"
                value={key.localColumn}
                onChange={(e) => replaceJoinKey(ki, { ...key, localColumn: e.target.value })}
                data-testid={`relationship-${testIndex}-join-local-${ki}`}
              >
                <option value="">Select…</option>
                {(sourceColumns ?? []).map((c) => <option key={c.name} value={c.name}>{c.name}</option>)}
              </select>
            </Field>
            <span className="faint">=</span>
            <Field label="Foreign column">
              <select
                className="select sm"
                value={key.foreignColumn}
                disabled={!relationship.table}
                onChange={(e) => replaceJoinKey(ki, { ...key, foreignColumn: e.target.value })}
                data-testid={`relationship-${testIndex}-join-foreign-${ki}`}
              >
                <option value="">Select…</option>
                {(foreignColumns ?? []).map((c) => <option key={c.name} value={c.name}>{c.name}</option>)}
              </select>
            </Field>
            {relationship.joinKeys.length > 1 && (
              <button
                type="button"
                className="btn-link quiet"
                onClick={() => removeJoinKey(ki)}
                data-testid={`remove-relationship-${testIndex}-join-${ki}`}
              >
                Remove
              </button>
            )}
          </div>
        ))}
        <button type="button" className="btn btn-sm" style={{ alignSelf: 'flex-start' }} onClick={addJoinKey} data-testid={`add-relationship-${testIndex}-join`}>
          Add join key
        </button>
      </div>
    </div>
  )
}
