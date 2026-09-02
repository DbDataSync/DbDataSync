import { useState } from 'react'
import { CodeEditor } from '../../components/CodeEditor'
import { Field } from '../../components/Field'
import { usePreviewQuery } from '../../api/hooks'
import type { ColumnMetadata, QueryPreviewResult } from '../../api/types'

/**
 * The source tab, for a reader whose configuration is a query.
 *
 * Replaces the schema and table pickers — the two fields a query-first source genuinely has no answer
 * for — with the statement itself and a button that runs it. Connection and database stay above,
 * because a query still has to run *somewhere* and config requires both to resolve; what it does not
 * have is a table in a catalog to point at.
 *
 * **Preview is the point of this panel, not a convenience on it.** For every other reader the mapping
 * editor can show an operator their source: pick a table and its columns are listed. Here the columns
 * exist only once the query has run, so without this the operator would be writing SQL blind and
 * finding out on the first pass — and the column-mapping tab would have nothing to offer either,
 * which is why the result is lifted to the form rather than kept in this component.
 */
export function QuerySourcePanel({
  query, onChange, connectionName, onColumns, testIdPrefix,
}: {
  query: string
  onChange: (next: string) => void
  /** Where Preview runs. Empty until the replication's source endpoint or an override names one. */
  connectionName: string
  /** The result's columns, handed up so the column-mapping tab can map what the query actually returns. */
  onColumns: (columns: ColumnMetadata[]) => void
  testIdPrefix: string
}) {
  const preview = usePreviewQuery()
  const [result, setResult] = useState<QueryPreviewResult | null>(null)

  const run = async () => {
    const next = await preview.mutateAsync({ connectionName, query })
    setResult(next)

    // Only on a query that ran. A failed preview leaves the last good columns in place rather than
    // emptying the column-mapping tab underneath an operator who is mid-edit and mid-typo.
    if (!next.error)
      onColumns(next.columns.map((name) => ({
        // The result set says what the columns are called and nothing else — a preview reports no
        // types, no nullability and no keys. Stated as blanks rather than guessed from the sample
        // values, which would be a type inferred from three rows and wrong on the fourth.
        name, nativeType: '', isNullable: true, isPrimaryKey: false, isIdentity: false,
      })))
  }

  return (
    <>
      <Field label="Query">
        <span className="hint">
          Every row this returns is an insert — a scan sees what exists, and the writer reconciles what
          does not. To segment it, reference the bounds in your own <code>WHERE</code>:{' '}
          <code>{'{{segmentColumn}}'}</code> with <code>{'{{segmentMin}}'}</code>/
          <code>{'{{segmentMax}}'}</code>, or with <code>{'{{segmentValues}}'}</code> for a list.
        </span>
        <CodeEditor
          value={query}
          language="sql"
          onChange={onChange}
          minLines={6}
          testId={`${testIdPrefix}-query-editor`}
        />
      </Field>

      <div className="row" style={{ gap: 8 }}>
        <button
          type="button"
          className="btn btn-sm"
          disabled={!connectionName || !query.trim() || preview.isPending}
          onClick={run}
          data-testid={`${testIdPrefix}-query-preview-button`}
        >
          {preview.isPending ? 'Running…' : 'Preview'}
        </button>
        <span className="hint">
          {!connectionName
            ? 'Choose a connection first — a preview runs this against a real system.'
            : result
              ? result.source
              : 'Runs what is in the editor now, saved or not.'}
        </span>
      </div>

      {preview.error && (
        <span className="banner warn" role="alert" data-testid={`${testIdPrefix}-query-preview-failed`}>
          {(preview.error as Error).message}
        </span>
      )}

      {result && <PreviewGrid result={result} testId={`${testIdPrefix}-query-preview`} />}
    </>
  )
}

/**
 * Columns and the first few rows, and nothing else.
 *
 * A query the engine rejected is shown as what it said rather than as an empty grid: somebody writing
 * SQL gets it wrong several times on the way to right, and the message is the useful half of each of
 * those attempts.
 */
function PreviewGrid({ result, testId }: { result: QueryPreviewResult; testId: string }) {
  if (result.error) {
    return (
      <span className="banner warn" role="alert" data-testid={`${testId}-error`}>
        {result.error}
      </span>
    )
  }

  if (result.columns.length === 0)
    return <div className="empty" data-testid={`${testId}-empty`}>That query returned no columns.</div>

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }} data-testid={testId}>
      {/* Its own scroll container: a wide result must not make the whole editor scroll sideways. */}
      <div style={{ overflowX: 'auto', border: '1px solid var(--row-edge)', borderRadius: 4 }}>
        <table className="preview-grid" style={{ borderCollapse: 'collapse', width: '100%' }}>
          <thead>
            <tr>
              {result.columns.map((c) => (
                <th
                  key={c}
                  style={{
                    textAlign: 'left', padding: '5px 9px', whiteSpace: 'nowrap',
                    font: '600 11.5px var(--ui)', color: 'var(--ink-4)',
                    borderBottom: '1px solid var(--row-edge)', background: 'var(--sunken)',
                  }}
                >
                  {c}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {result.rows.map((row, i) => (
              <tr key={i}>
                {row.map((cell, j) => (
                  <td
                    key={j}
                    style={{
                      padding: '4px 9px', whiteSpace: 'nowrap',
                      font: '12px var(--mono)', borderBottom: '1px solid var(--row-edge)',
                      // A NULL and an empty string have to look different, or a preview of a column
                      // full of blanks says nothing about which of the two it is full of.
                      color: cell === null ? 'var(--ink-5)' : 'var(--ink-2)',
                    }}
                  >
                    {cell === null ? 'NULL' : cell}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <span className="hint" data-testid={`${testId}-note`}>
        {result.rows.length === 0
          ? 'No rows — the columns above are still what this query produces.'
          : `${result.rows.length} row${result.rows.length === 1 ? '' : 's'}${result.truncated ? ', capped for this preview' : ''}`}
      </span>
    </div>
  )
}
