import { useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { useBulkLoadHistory, useTableMappings } from '../../api/hooks'
import type { BulkLoadState } from '../../api/types'

const COLUMNS = '1.1fr 1.1fr 1fr 1.4fr .9fr'

const STATE_DOT: Record<BulkLoadState, string> = {
  Running: 'dot-ok',
  Completed: 'dot-ok',
  CompletedWithFailures: 'dot-warn',
}

const STATE_WORD: Record<BulkLoadState, string> = {
  Running: 'running',
  Completed: 'completed',
  CompletedWithFailures: 'completed with failures',
}

/**
 * Every past bulk load this replication has run, across every mapping — Monitoring's fourth sub-tab,
 * see phase 139. `BulkLoadBatchStore.GetRecentBulkLoads`'s own doc comment named this screen as a
 * future consumer since phase 107; nothing built it until now.
 *
 * **Static, like `PauseHistoryPanel` beside it — not live-polled like `RunsPanel`.** The Monitoring →
 * Current Status card (`BulkLoadProgressCard`) stays the one live view of an in-flight load; this is a
 * "what happened" audit log an operator reloads by hand, so `useBulkLoadHistory` carries no
 * `refetchInterval` at all.
 *
 * **Pagination is `RunsPanel`'s own cursor-stack shape**, reused as an interaction pattern rather than
 * a shared component — this screen has no live-polling half to gate on page number the way that
 * panel's does. `cursorStack[0]` is always `null` (page one); "Older" pushes the page just read's own
 * `nextCursor`, "Newer" pops back to the one before it. Changing the mapping filter resets the stack to
 * `[null]`, the same way `RunsPanel.changeMapping` does — a cursor minted under one filter means
 * nothing replayed against another's history (see `BulkLoadHistoryCursorCodec`), so there is no reason
 * to keep it around across a filter change even before the server would reject it.
 */
export function BulkLoadHistoryPanel({ replicationName }: { replicationName: string }) {
  const [mappingFilter, setMappingFilter] = useState('')
  const [cursorStack, setCursorStack] = useState<(string | null)[]>([null])
  const cursor = cursorStack[cursorStack.length - 1]
  const onFirstPage = cursorStack.length === 1

  const changeMapping = (value: string) => {
    setMappingFilter(value)
    setCursorStack([null])
  }

  const { data: page, isLoading, error } = useBulkLoadHistory(replicationName, {
    mappingName: mappingFilter || undefined,
    cursor: cursor ?? undefined,
  })
  const { data: mappingNames } = useTableMappings(replicationName)

  const batches = page?.batches ?? []

  return (
    <div className="card flush" data-testid="bulk-load-history-table">
      <ErrorBanner error={error} />

      <div className="card-head tight" style={{ flexWrap: 'wrap', rowGap: 8 }}>
        <span className="card-title sm">Bulk load history</span>
        <select
          className="select sm"
          style={{ flex: 'none', width: 170 }}
          value={mappingFilter}
          onChange={(e) => changeMapping(e.target.value)}
          data-testid="bulk-load-history-filter-mapping"
          aria-label="Filter by mapping"
        >
          <option value="">All mappings</option>
          {(mappingNames ?? []).map((m) => <option key={m} value={m}>{m}</option>)}
        </select>

        <span className="spacer row" style={{ gap: 10 }}>
          <button
            type="button"
            className="btn btn-sm"
            disabled={onFirstPage}
            onClick={() => setCursorStack((stack) => stack.slice(0, -1))}
            data-testid="bulk-load-history-page-newer"
          >
            ◂ Newer
          </button>
          <button
            type="button"
            className="btn btn-sm"
            disabled={!page?.nextCursor}
            onClick={() => setCursorStack((stack) => (page?.nextCursor ? [...stack, page.nextCursor] : stack))}
            data-testid="bulk-load-history-page-older"
          >
            Older ▸
          </button>
        </span>
      </div>

      <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
        <span>Started</span><span>Mapping</span><span>Segments</span><span>Rows copied</span><span>State</span>
      </div>
      {isLoading && <div className="empty">Loading…</div>}
      {!isLoading && batches.length === 0 && (
        <div className="empty">
          {mappingFilter || !onFirstPage ? 'No bulk loads match this filter.' : 'No bulk loads yet.'}
        </div>
      )}
      {batches.map((b) => {
        const segments =
          `${b.segmentsSucceeded} / ${b.segmentCount}` +
          (b.segmentsFailed > 0 ? ` · ${b.segmentsFailed} failed` : '')

        return (
          <div
            key={b.batchId}
            className="grid-row short"
            style={{ gridTemplateColumns: COLUMNS, gap: 14 }}
            data-testid="bulk-load-history-row"
            data-batch-id={b.batchId}
            data-bulk-load-state={b.state}
          >
            {/* StartedAtUtc is null for a batch whose segments are all still Queued — CreatedAtUtc is
                when it was actually enqueued, so that is what stands in rather than a blank cell. */}
            <span className="dim">{new Date(b.startedAtUtc ?? b.createdAtUtc).toLocaleString()}</span>
            <span className="mono sm">{b.mappingName}</span>
            <span data-testid={`bulk-load-history-segments-${b.batchId}`}>{segments}</span>
            <span data-testid={`bulk-load-history-rows-copied-${b.batchId}`}>
              {b.rowsCopied.toLocaleString()}
              {b.estimatedRows != null && (
                <span className="faint sm"> / ≈ {b.estimatedRows.toLocaleString()}</span>
              )}
              {b.estimateCaveat && (
                <span className="faint sm" title={b.estimateCaveat}> *</span>
              )}
            </span>
            <span className="status" data-testid={`bulk-load-history-state-${b.batchId}`}>
              <span className={`dot ${STATE_DOT[b.state]}`} />
              {STATE_WORD[b.state]}
            </span>
          </div>
        )
      })}
    </div>
  )
}
