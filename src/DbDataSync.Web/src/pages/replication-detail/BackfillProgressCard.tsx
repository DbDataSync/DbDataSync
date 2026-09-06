import { RefreshCountdown } from '../../components/RefreshCountdown'
import { RunKindBadge } from '../../components/StatusBadge'
import { MONITORING_REFRESH_MS, useRecentBackfills } from '../../api/hooks'
import type { BackfillState } from '../../api/types'

/** How long a finished batch stays on the card so the final numbers are seen before it clears. */
const GRACE_MS = 5 * 60_000

const STATE_DOT: Record<BackfillState, string> = {
  Running: 'dot-ok',
  Completed: 'dot-ok',
  CompletedWithFailures: 'dot-warn',
}

const STATE_WORD: Record<BackfillState, string> = {
  Running: 'running',
  Completed: 'completed',
  CompletedWithFailures: 'completed with failures',
}

/**
 * The active backfill's progress, on Monitoring → Current Status. A backfill is queued as one
 * independently-scheduled run per segment; the server rolls them back up by the batch id minted at
 * enqueue, and this shows that roll-up — rows copied so far against a catalog-statistics estimate of
 * the whole table, and how many segments are done.
 *
 * **The newest batch only, and only while it matters** — running, or finished within the last few
 * minutes. A list of past backfills is a future Batch Load History screen, not this card; here it is
 * "what is happening right now, and why is the table not caught up yet".
 *
 * No progress bar: "rows copied" moves in per-segment steps (a segment run records its total only on
 * completion), and a bar sweeping in jumps reads as broken. The number and the segment count carry
 * it.
 */
export function BackfillProgressCard({ replicationName }: { replicationName: string }) {
  const { data, dataUpdatedAt } = useRecentBackfills(replicationName)
  const batch = data?.[0]

  if (!batch) return null

  const worthShowing = batch.state === 'Running' || msSince(batch.lastActivityUtc) < GRACE_MS
  if (!worthShowing) return null

  const intervalMs = batch.state === 'Running' ? 2_000 : MONITORING_REFRESH_MS

  const segments =
    `${batch.segmentsSucceeded} / ${batch.segmentCount}` +
    (batch.segmentsFailed > 0 ? ` · ${batch.segmentsFailed} failed` : '')

  return (
    <div className="card" data-testid="backfill-progress-card" data-backfill-state={batch.state}>
      <div className="card-head tight">
        <span className="card-title sm">Batch reload</span>
        <RunKindBadge kind="Backfill" />
        <span className="card-note">{batch.mappingName}</span>
        <span className="status">
          <span className={`dot ${STATE_DOT[batch.state]}`} />
          {STATE_WORD[batch.state]}
        </span>
        <span className="spacer">
          <RefreshCountdown
            label="Batch reload"
            dataUpdatedAt={dataUpdatedAt}
            intervalMs={intervalMs}
            testId="backfill-countdown"
          />
        </span>
      </div>
      <div className="card-body">
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
          <Figure
            label="Rows copied"
            value={batch.rowsCopied.toLocaleString()}
            testId="backfill-rows-copied"
          />
          <Figure
            label="Estimated total"
            // "Unknown" is not zero — a query source or an ODBC engine has no catalog to ask.
            value={batch.estimatedRows != null ? `≈ ${batch.estimatedRows.toLocaleString()}` : 'Unknown'}
            testId="backfill-estimated-total"
          />
          <Figure label="Segments" value={segments} testId="backfill-segments" />
          <Figure label="Started" value={batch.startedAtUtc ? formatAgo(batch.startedAtUtc) : 'not started'} />
        </div>
        <span className="hint">
          {batch.segmentsSucceeded} of {batch.segmentCount} segments complete
          {batch.segmentsRunning > 0 ? `, ${batch.segmentsRunning} running` : ''}
        </span>
        {batch.estimateCaveat && (
          <span className="hint" data-testid="backfill-estimate-caveat">
            Estimated total is the whole table — this reload ignores the mapping&rsquo;s row filter.
          </span>
        )}
      </div>
    </div>
  )
}

/** Local, matching StatusCard/MetricsCard/MonitoringPanel — the codebase keeps its own per file. */
function Figure({ label, value, testId }: { label: string; value: string; testId?: string }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }} data-testid={testId}>
      <span
        style={{
          font: '500 10.5px var(--ui)',
          color: 'var(--ink-4)',
          textTransform: 'uppercase',
          letterSpacing: '.04em',
        }}
      >
        {label}
      </span>
      <span style={{ font: '500 13px var(--ui)', color: 'var(--ink)' }}>{value}</span>
    </div>
  )
}

function formatAgo(iso: string) {
  const seconds = msSince(iso) / 1000
  if (seconds < 90) return `${Math.round(seconds)}s ago`
  return seconds < 5400 ? `${Math.round(seconds / 60)}m ago` : `${Math.round(seconds / 3600)}h ago`
}

/** Milliseconds since an ISO timestamp, or `Infinity` when there is none — a batch whose segments
 * are all still queued has no activity to be "within the grace window" of. */
function msSince(iso: string | null): number {
  return iso ? Math.max(0, Date.now() - new Date(iso).getTime()) : Number.POSITIVE_INFINITY
}
