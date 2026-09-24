import { useEffect, useState } from 'react'
import { CodeEditor } from '../../components/CodeEditor'
import { PreviewGrid } from '../../components/PreviewGrid'
import { usePreviewQuery } from '../../api/hooks'
import type { ColumnMetadata, QueryPreviewResult } from '../../api/types'

/** The SPA's own fixed choice (phase 193S) — not an arbitrary sample size. `0` means "shape only, no
 * data", which is what serves metadata capture through this same preview, with no separate mechanism. */
const MAX_ROWS_CHOICES = [0, 10, 50] as const
type MaxRowsChoice = (typeof MAX_ROWS_CHOICES)[number]

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
 *
 * **Open state is controlled** (phase 193S), not local: the stale-metadata guard at save time needs to
 * be able to reopen this same dialog for its own "run preview instead" exit, which it cannot do to
 * state this component owns privately.
 */
export function QuerySourcePanel({
  query, onChange, allowSubquery, onAllowSubqueryChange, connectionName, onColumns,
  open, onOpenChange, testIdPrefix,
}: {
  query: string
  onChange: (next: string) => void
  /** Whether `query` may be wrapped as a subquery — see `SourceTableSpec.AllowSubquery`'s own doc
   * comment. Editable here, next to the preview it governs. */
  allowSubquery: boolean
  onAllowSubqueryChange: (next: boolean) => void
  /** Where Preview runs. Empty until the replication's source endpoint or an override names one. */
  connectionName: string
  /** The result's columns, handed up so the column-mapping tab can map what the query actually returns
   * — together with the exact query text that produced them, which is what the save-time staleness
   * guard compares the draft against. */
  onColumns: (columns: ColumnMetadata[], previewedQuery: string) => void
  open: boolean
  onOpenChange: (open: boolean) => void
  testIdPrefix: string
}) {
  return (
    <div className="row" style={{ gap: 8, alignItems: 'center' }}>
      <button
        type="button"
        className="btn btn-sm"
        onClick={() => onOpenChange(true)}
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
          allowSubquery={allowSubquery}
          onAllowSubqueryChange={onAllowSubqueryChange}
          connectionName={connectionName}
          onColumns={onColumns}
          onClose={() => onOpenChange(false)}
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
  query, onChange, allowSubquery, onAllowSubqueryChange, connectionName, onColumns, onClose, testIdPrefix,
}: {
  query: string
  onChange: (next: string) => void
  allowSubquery: boolean
  onAllowSubqueryChange: (next: boolean) => void
  connectionName: string
  onColumns: (columns: ColumnMetadata[], previewedQuery: string) => void
  onClose: () => void
  testIdPrefix: string
}) {
  const preview = usePreviewQuery()
  const [result, setResult] = useState<QueryPreviewResult | null>(null)
  const [maxRows, setMaxRows] = useState<MaxRowsChoice>(10)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const runWith = async (subquery: boolean) => {
    const next = await preview.mutateAsync({ connectionName, query, maxRows, allowSubquery: subquery })
    setResult(next)

    // Only on a query that ran. A failed preview leaves the last good columns in place rather than
    // emptying the column-mapping tab underneath an operator who is mid-edit and mid-typo.
    if (!next.error)
      onColumns(next.columnMetadata ?? next.columns.map((name) => ({
        // The provider's own schema call didn't come back (older server, or a provider that doesn't
        // implement it) — the same blank-guess fallback this feature is additive to, rather than a
        // type inferred from the sample values, which would be right by luck and wrong on row four.
        name, nativeType: '', isNullable: true, isPrimaryKey: false, isIdentity: false,
      })), query)
  }

  const run = () => runWith(allowSubquery)

  /**
   * The one-click recovery this phase asks for, and deliberately not automatic: a wrapped preview that
   * fails might simply be a query that cannot be used as a subquery, which is a real and expected
   * outcome, not a bug to route around silently. This flips the draft's own AllowSubquery — the same
   * setting a save would otherwise persist — and reruns; it does not run once and forget the setting
   * change, because disallowing subqueries here is a real, kept decision about this source.
   */
  const retryWithoutSubqueries = async () => {
    onAllowSubqueryChange(false)
    await runWith(false)
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
            what does not.
          </span>

          <CodeEditor
            value={query}
            language="sql"
            onChange={onChange}
            minLines={10}
            testId={`${testIdPrefix}-query-editor`}
          />

          <div className="row" style={{ gap: 14, alignItems: 'center', flexWrap: 'wrap' }}>
            <div className="row" style={{ gap: 7 }}>
              <button
                type="button"
                className={`toggle ${allowSubquery ? 'on' : ''}`}
                onClick={() => onAllowSubqueryChange(!allowSubquery)}
                aria-pressed={allowSubquery}
                data-testid={`${testIdPrefix}-query-allow-subquery-toggle`}
              />
              <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>
                Allow wrapping as a subquery (segmenting, relationships, and column transforms need this)
              </span>
            </div>

            <label className="row" style={{ gap: 6 }}>
              <span className="hint">Preview</span>
              <select
                className="select"
                value={maxRows}
                onChange={(e) => setMaxRows(Number(e.target.value) as MaxRowsChoice)}
                data-testid={`${testIdPrefix}-query-maxrows-select`}
              >
                <option value={0}>shape only, no rows</option>
                <option value={10}>10 rows</option>
                <option value={50}>50 rows</option>
              </select>
            </label>
          </div>

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

          {/* The message itself is PreviewGrid's own job (`${testId}-error`) — not repeated here, so a
              rejected query is shown once, the same way it always has been. This is only the recovery
              affordance the engine's own message can't offer on its own. */}
          {result?.error && allowSubquery && (
            <span className="hint">
              <button
                type="button"
                className="btn-link"
                onClick={retryWithoutSubqueries}
                disabled={preview.isPending}
                data-testid={`${testIdPrefix}-query-retry-without-subqueries`}
              >
                Retry without subqueries
              </button>
            </span>
          )}

          {result && <PreviewGrid result={result} testId={`${testIdPrefix}-query-preview`} />}
        </div>
      </div>
    </div>
  )
}
