import { useState } from 'react'
import { useColumns, useInferredColumnTypes } from '../../api/hooks'
import { EditableValue } from '../../components/EditableValue'
import type { ColumnMapping, ColumnMetadata, ResolvedRef } from '../../api/types'

interface Props {
  replicationName: string
  /** Undefined for a mapping that has not been saved yet — there is nothing on disk to infer from. */
  mappingName: string | undefined
  target: ResolvedRef
  mappings: ColumnMapping[]
  onChange: (mappings: ColumnMapping[]) => void
  /**
   * False when the target names a table provisioning has yet to create — see below. `undefined` while
   * the catalog is still loading, which is neither: asking for the columns of a table that may not be
   * there is a request that 404s, and assuming it is missing would flash the source's columns at
   * someone who picked an existing table.
   */
  targetExists: boolean | undefined
  /**
   * The source's columns, resolved by the form above rather than fetched here.
   *
   * Passed in because there is now more than one way to know them: a table source reads the catalog,
   * and a query source has no catalog to read — its columns are whatever its last preview returned.
   * The form owns that decision because it owns the preview's result; this editor only needs the
   * answer. React Query serves the catalog case from the same cached fetch either way.
   */
  sourceColumns: ColumnMetadata[] | undefined
  /** A query source has no catalog, so an empty list here means "not previewed yet" rather than
   * "this table has no columns" — which are different things to tell an operator. */
  querySource: boolean
}

const COLUMNS = '1fr 22px 1fr 0.95fr 1.15fr 74px'

/**
 * Once both sides have a table, lists the target's columns and lets each be fed from a source column,
 * auto-suggesting a same-name match on first load. The design adds the source column's SQL type
 * beside its name and a PK badge on the target — both come from the metadata endpoint, so both are
 * real.
 *
 * Transform is a SQL expression in the *source's* dialect, evaluated by the source engine — not by
 * this process and not by the target. `{{column}}` stands for the column being transformed, and has
 * to, because the reference is not spelled the same way in every reader's statement (the Change
 * Tracking reader joins the source table under an alias). See phase 22.
 *
 * **When the target does not exist yet, its columns are the source's.** There is no catalog to read,
 * and inventing an empty list would leave the operator with nothing to map — but this is not merely a
 * convenience to fill the screen. `ProvisioningService` builds the `CREATE TABLE` from the mapping's
 * *column mappings*, so what is listed here is literally what gets created. Showing the source's
 * columns is the only answer that makes the table that appears match the table that was described.
 */
export function ColumnMappingEditor({
  replicationName, mappingName, target, mappings, onChange, targetExists,
  sourceColumns, querySource,
}: Props) {
  // Not asked for at all when the table is not there: the request would 404 and be retried, and the
  // answer is already known.
  const { data: catalogTargetColumns } = useColumns(
    target.connectionName, target.database, target.schema, targetExists === true ? target.table : undefined)
  const targetColumns = targetExists === false ? sourceColumns : catalogTargetColumns
  const { data: inferred } = useInferredColumnTypes(replicationName, mappingName)
  const [columnToAdd, setColumnToAdd] = useState('')

  // The same-name auto-suggestion used to live here, and moved up to TableMappingForm when this
  // editor became one tab among several (phase 64): a mapping whose columns were never suggested
  // cannot be saved, and the operator would have had to visit this tab to make Save work. A
  // suggestion about the mapping belongs with the mapping, not with whichever tab is open.

  // target.table is checked separately: with no target chosen at all, targetColumns falls back to the
  // source's and would otherwise render a full editor for a table nobody has named.
  if (!sourceColumns || !targetColumns || !target.table) {
    return (
      <div className="card">
        <div className="card-head tight"><span className="card-title sm">Column mappings</span></div>
        <div className="empty">Select a source and target table to map columns.</div>
      </div>
    )
  }

  // Empty means two different things, and saying so is the difference between an operator knowing
  // what to do next and staring at a blank grid. A query source has no catalog to have been empty.
  if (querySource && sourceColumns.length === 0) {
    return (
      <div className="card">
        <div className="card-head tight"><span className="card-title sm">Column mappings</span></div>
        <div className="empty" data-testid="column-mappings-awaiting-preview">
          Preview the source query — on the Source card above — and its result columns become the ones
          to map here.
        </div>
      </div>
    )
  }

  const typeOf = (name: string) => sourceColumns.find((c) => c.name === name)?.nativeType
  const inferenceFor = (sourceColumn: string) => inferred?.find((i) => i.sourceColumn === sourceColumn)
  // For a table that does not exist yet this reads the source's key, which is what the generated
  // CREATE TABLE will carry over.
  const isPk = (name: string) => targetColumns.find((c) => c.name === name)?.isPrimaryKey
  const mapped = new Set(mappings.map((m) => m.targetColumn))
  const unmapped = targetColumns.filter((tc) => !mapped.has(tc.name))

  const updateRow = (index: number, patch: Partial<ColumnMapping>) =>
    onChange(mappings.map((m, i) => (i === index ? { ...m, ...patch } : m)))

  /**
   * Renaming a target column is not the same edit as correcting a typo in one, and which of the two
   * it is depends entirely on whether the target already has a column under the old name.
   *
   * If it does, the rename is recorded so provisioning can `RENAME` it and the existing rows keep
   * their values — dropping and re-adding would silently empty the column. If it does not (a target
   * table that has yet to be created, or a column only ever named in config), there is nothing to
   * rename and recording a step would ask provisioning to rename a column that was never there.
   */
  const renameTarget = (index: number, next: string | null) => {
    const from = mappings[index].targetColumn
    if (!next || next === from) return

    const onTarget = targetExists === true && targetColumns.some((c) => c.name === from)
    updateRow(index, {
      targetColumn: next,
      renames: onTarget
        ? [...(mappings[index].renames ?? []), { from, to: next, applied: false }]
        : mappings[index].renames,
    })
  }

  const autoMap = () => {
    const sourceNames = new Set(sourceColumns.map((c) => c.name))
    onChange(targetColumns.filter((tc) => sourceNames.has(tc.name))
      .map((tc) => ({ sourceColumn: tc.name, targetColumn: tc.name, transform: null })))
  }

  return (
    <div className="card flush" data-testid="column-mappings-table">
      <div className="card-head tight">
        <span className="card-title sm">Column mappings</span>
        <span className="card-note">
          {mappings.length} of {targetColumns.length} target columns mapped ·{' '}
          {targetExists === false
            ? <>the target does not exist yet, so these are the source's columns — they are what will be created</>
            : <>transforms are SQL in the source's dialect, with <code>{'{{column}}'}</code> for the column itself</>}
        </span>
        <button type="button" className="btn btn-sm spacer" onClick={autoMap}>Auto-map by name</button>
      </div>

      <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 0, height: 29 }}>
        <span>Source column</span><span /><span>Target column</span><span>Target type</span>
        <span>Transform</span><span />
      </div>

      {mappings.map((m, i) => (
        <div key={i} className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 0 }}>
          <span className="row" style={{ gap: 7 }}>
            <select
              className="select sm"
              style={{ maxWidth: 190 }}
              value={m.sourceColumn}
              onChange={(e) => updateRow(i, { sourceColumn: e.target.value })}
              data-testid={`column-mapping-source-${i}`}
            >
              {/* A stored value the freshly loaded metadata does not have is shown as itself, marked.
                  Without this option present the browser silently renders the *first* one instead —
                  the state is unchanged, only the display lies — so a mapping could be resaved
                  against a column the operator never chose and never saw change. */}
              {!sourceColumns.some((c) => c.name === m.sourceColumn) && (
                <option value={m.sourceColumn}>
                  {m.sourceColumn ? `${m.sourceColumn} — not on the source` : 'Select…'}
                </option>
              )}
              {sourceColumns.map((c) => <option key={c.name} value={c.name}>{c.name}</option>)}
            </select>
            <span className="faint">{typeOf(m.sourceColumn)}</span>
          </span>
          <span className="faint">→</span>
          <span className="row" style={{ gap: 7, minWidth: 0 }}>
            <EditableValue
              value={m.targetColumn}
              label={`${m.targetColumn} target column name`}
              onChange={(next) => renameTarget(i, next)}
              testId={`column-mapping-target-${i}`}
            />
            {isPk(m.targetColumn) && <span className="badge badge-accent">PK</span>}
            {(m.renames?.some((r) => !r.applied) ?? false) && (
              <span
                className="badge"
                title={`Will be renamed from ${m.renames![0].from} when provisioning is applied.`}
              >
                RENAMED
              </span>
            )}
            {/* Same honesty on the other side: a target column the catalog does not have is a mapping
                that will fail at staging, and saying so here beats finding out on the next pass. */}
            {targetExists === true && !targetColumns.some((c) => c.name === m.targetColumn) && (
              <span className="badge" title="This column is not on the target table.">MISSING</span>
            )}
          </span>
          <span title={inferenceFor(m.sourceColumn)?.fidelity ?? inferenceFor(m.sourceColumn)?.problem ?? undefined}>
            <EditableValue
              value={m.targetType}
              // Empty means "whatever the source's type maps to", so the inference is the placeholder
              // rather than a prefilled value: prefilling it would turn every row into an override and
              // freeze today's answer against a source column that later changes.
              placeholder={inferenceFor(m.sourceColumn)?.targetType ?? 'inferred'}
              label={`${m.targetColumn} target type`}
              onChange={(next) => updateRow(i, { targetType: next })}
              monospace
              testId={`column-mapping-type-${i}`}
            />
          </span>
          <span style={{ minWidth: 0 }}>
            <EditableValue
              value={m.transform}
              placeholder="none"
              label={`${m.targetColumn} transform`}
              onChange={(next) => updateRow(i, { transform: next })}
              monospace
              testId={`column-mapping-transform-${m.targetColumn}`}
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
