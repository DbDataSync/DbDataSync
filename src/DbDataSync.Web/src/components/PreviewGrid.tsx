import type { QueryPreviewResult } from '../api/types'

/**
 * Columns and the first few rows, and nothing else.
 *
 * A query the engine rejected is shown as what it said rather than as an empty grid: somebody writing
 * SQL gets it wrong several times on the way to right, and the message is the useful half of each of
 * those attempts.
 *
 * Extracted from `QuerySourcePanel`'s own `PreviewGrid` so `ConnectionTestCard`'s test-query result can
 * render the same grid rather than a second implementation that has to keep agreeing with it.
 */
export function PreviewGrid({ result, testId }: { result: QueryPreviewResult; testId: string }) {
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
