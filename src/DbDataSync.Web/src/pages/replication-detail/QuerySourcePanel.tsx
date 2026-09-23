import { useEffect, useState } from 'react'
import { CodeEditor } from '../../components/CodeEditor'
import { PreviewGrid } from '../../components/PreviewGrid'
import { usePreviewQuery } from '../../api/hooks'
import type { ColumnMetadata, QueryPreviewResult } from '../../api/types'

/**
 * The source tab, for a reader whose configuration is a query.
 *
 * Replaces the schema and table pickers — the two fields a query-first source genuinely has no answer
 * for — with a button that opens the statement in a popup. Connection and database stay on the card,
 * because a query still has to run *somewhere* and config requires both to resolve; what moves into
 * the popup is everything that has no meaning until the button is pressed: the instructions, the
 * editor, and the preview. The source card otherwise reads the same as every other reader's — one row
 * that says what's configured, not the configuration itself sitting open on the page.
 *
 * **Preview is the point of the popup, not a convenience in it.** For every other reader the mapping
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
  const [open, setOpen] = useState(false)

  return (
    <div className="row" style={{ gap: 8, alignItems: 'center' }}>
      <button
        type="button"
        className="btn btn-sm"
        onClick={() => setOpen(true)}
        data-testid={`${testIdPrefix}-query-open-button`}
      >
        Source Query
      </button>
      <span className="hint">
        {query.trim() ? `${query.trim().split('\n').length} line query` : 'No query written yet'}
      </span>

      {open && (
        <QueryEditorDialog
          query={query}
          onChange={onChange}
          connectionName={connectionName}
          onColumns={onColumns}
          onClose={() => setOpen(false)}
          testIdPrefix={testIdPrefix}
        />
      )}
    </div>
  )
}

/**
 * The popup itself. State for the last preview result lives here, not in the panel above, so closing
 * and reopening starts clean rather than showing a result from a session that may no longer match
 * what's in the editor — the same "don't imply a fact you can't back up" reasoning `PauseDialog` uses
 * for its own draft.
 */
function QueryEditorDialog({
  query, onChange, connectionName, onColumns, onClose, testIdPrefix,
}: {
  query: string
  onChange: (next: string) => void
  connectionName: string
  onColumns: (columns: ColumnMetadata[]) => void
  onClose: () => void
  testIdPrefix: string
}) {
  const preview = usePreviewQuery()
  const [result, setResult] = useState<QueryPreviewResult | null>(null)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

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
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose() }}>
      <div
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-label="Source query"
        data-testid={`${testIdPrefix}-query-dialog`}
        style={{ width: 'min(920px, 92vw)' }}
      >
        <div className="card-head">
          <span className="card-title">Source query</span>
          <button
            type="button"
            className="btn btn-sm"
            onClick={onClose}
            data-testid={`${testIdPrefix}-query-close-button`}
          >
            Close
          </button>
        </div>
        <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <span className="hint">
            Every row this returns is an insert — a scan sees what exists, and the writer reconciles
            what does not. To segment it, reference the bounds in your own <code>WHERE</code>:{' '}
            <code>{'{{segmentColumn}}'}</code> with <code>{'{{segmentMin}}'}</code>/
            <code>{'{{segmentMax}}'}</code>, or with <code>{'{{segmentValues}}'}</code> for a list.
          </span>

          <CodeEditor
            value={query}
            language="sql"
            onChange={onChange}
            minLines={10}
            testId={`${testIdPrefix}-query-editor`}
          />

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
        </div>
      </div>
    </div>
  )
}
