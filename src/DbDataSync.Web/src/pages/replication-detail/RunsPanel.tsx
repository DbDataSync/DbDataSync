import { useEffect, useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { RunDetailsDialog } from '../../components/RunDetailsDialog'
import { RunKindBadge, StatusBadge } from '../../components/StatusBadge'
import { BackfillForm } from './BackfillForm'
import {
  MONITORING_REFRESH_MS, useCancelRun, useInvalidateRunHistory, useRunHistory,
  useRunWatermarkTimes, useTriggerRun, useResyncRun,
} from '../../api/hooks'
import { RefreshCountdown } from '../../components/RefreshCountdown'
import { useRunHub } from '../../api/useRunHub'
import { TimingDetail, WatermarkCell } from '../../components/RunFigures'
import { processingTime, queueTime } from '../../components/runTimes'
import type { TaskRunRecord } from '../../api/types'

/** A command sent down from the chrome's Backfill…/Run Now buttons. */
export interface RunsCommand {
  kind: 'backfill' | 'run'
  nonce: number
}

const COLUMNS = '1.05fr .65fr .85fr .7fr .5fr .55fr .6fr .7fr 1.15fr .85fr 78px'
type Filter = 'all' | 'failed' | 'backfills'

/**
 * The tight poll while a run is being watched live, unchanged since it was added.
 *
 * **A backstop, not the mechanism.** The hub pushes a run's log lines and its completion; this exists
 * because the final `TaskRuns` row is written by a different process from the one that pushed, and
 * the row's counts have to catch up. It stays faster than the panel's ordinary cadence for exactly
 * as long as somebody is watching one run.
 */
const LIVE_WATCH_MS = 1500

const clock = (iso: string | null) => (iso ? new Date(iso).toLocaleTimeString() : '—')

/**
 * The run history — Monitoring's **Run History** sub-tab since phase 103, formerly its own top-level
 * Runs tab. No `.pane` of its own: `MonitoringSection` owns that now, the same way it owns the pane
 * for Current Status beside it. The countdown that used to sit in the shared shell chrome lives in
 * this panel's own "Run history" card-head instead, beside the filter chips.
 */
export function RunsPanel({ replicationName, command }: { replicationName: string; command: RunsCommand | null }) {
  const [activeRunId, setActiveRunId] = useState<string | undefined>(undefined)
  // Which traced run has its timing open. One at a time: this is read to answer a question about one
  // pass, and several expanded at once would push the rest of the history off the screen.
  const [expandedRunId, setExpandedRunId] = useState<string | null>(null)
  const [showBackfill, setShowBackfill] = useState(false)
  const [filter, setFilter] = useState<Filter>('all')
  // The one run whose details are open in the popup — see RunDetailsDialog. Every run can open it,
  // not only a failed one. Independent of expandedRunId, which is only ever a traced run's stage
  // timings inline under its row.
  const [detailsRun, setDetailsRun] = useState<TaskRunRecord | null>(null)

  const isWatching = !!activeRunId

  // Ten seconds ordinarily; the live-watch backstop while somebody is watching a run, because it is
  // the faster of the two and a query has one interval.
  //
  // **Layered onto the hub, not in place of it** (phase 88). The panel used to poll only while
  // watching and otherwise wait for a push — which is right for a run that this browser started, and
  // silent about a run started by the scheduler, by the CLI, or by somebody else's browser. The hub
  // still delivers everything it delivered before, and the poll is what makes the list correct for
  // a tab nobody has touched. The two cannot storm each other: react-query restarts the interval
  // from the moment data lands, so a push-driven invalidation pushes the next poll ten seconds out
  // rather than racing it.
  const interval = isWatching ? LIVE_WATCH_MS : MONITORING_REFRESH_MS
  const { data: runs, error: historyError, dataUpdatedAt } = useRunHistory(replicationName, interval)

  // Deliberately the steady cadence even while watching: these timestamps come out of the polling
  // gate's own history, which is written once per scheduling tick, so asking every 1.5 seconds
  // would be re-deriving an answer nothing has changed. The key is nested under the run history's,
  // so the invalidation that fires when a run completes refreshes this too — which is the moment a
  // new watermark actually appears.
  const { data: watermarkTimes } = useRunWatermarkTimes(replicationName, MONITORING_REFRESH_MS)
  const trigger = useTriggerRun(replicationName)
  const cancel = useCancelRun(replicationName)
  const resync = useResyncRun(replicationName)
  const { logLines, completed } = useRunHub(activeRunId)
  const invalidateRunHistory = useInvalidateRunHistory(replicationName)

  useEffect(() => {
    if (completed) {
      // Force a fresh history fetch now the final TaskRuns row is known to exist, rather than
      // waiting on the interval poll to land on one (see useInvalidateRunHistory).
      invalidateRunHistory()
      const timeout = setTimeout(() => setActiveRunId(undefined), 3000)
      return () => clearTimeout(timeout)
    }
  }, [completed, invalidateRunHistory])

  // The chrome's buttons live two components up, so they arrive as a command rather than a call.
  useEffect(() => {
    if (!command) return
    if (command.kind === 'backfill') {
      setShowBackfill(true)
      return
    }
    let cancelled = false
    trigger.mutateAsync().then(
      (result) => { if (!cancelled) setActiveRunId(result.runIds[0]) },
      () => {},
    )
    return () => { cancelled = true }
    // Keyed on the nonce so a repeat of the same command still fires.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [command?.nonce])

  const visible = (runs ?? []).filter((r) =>
    filter === 'failed' ? r.status === 'Failed'
    : filter === 'backfills' ? r.runKind === 'Backfill'
    : true)

  return (
    <>
      <ErrorBanner error={historyError ?? trigger.error ?? cancel.error ?? resync.error} />

      {(isWatching || showBackfill) && (
        <div style={{ display: 'flex', gap: 14, alignItems: 'flex-start' }}>
          {isWatching && (
            <div className="card flush" style={{ flex: 1, minWidth: 0 }} data-testid="live-run-panel">
              <div className="card-head tight">
                <span className="card-title sm">Live run</span>
                {completed ? <StatusBadge status={completed.status} /> : <span className="status"><span className="dot dot-ok" />running</span>}
                {completed && (
                  // No duration here: the hub's completion payload carries counts and status only,
                  // not timestamps. History computes it from the stored run.
                  <span className="spacer mono" style={{ font: '400 11.5px var(--mono)', color: 'var(--ink-7)' }}>
                    {completed.rowsRead} row(s) read · {completed.rowsWritten} row(s) written
                    {completed.errorSummary ? ` · ${completed.errorSummary}` : ''}
                  </span>
                )}
                {!completed && (
                  <button className="btn btn-sm spacer" onClick={() => cancel.mutate(activeRunId!)}>Cancel</button>
                )}
              </div>
              <div style={{ padding: '12px 14px' }}>
                <div className="log" data-testid="live-log-viewer">
                  {logLines.length === 0 && <span className="log-time">Waiting for log output…</span>}
                  {logLines.map((line) => (
                    <span key={line.id} className={`log-line level-${line.level}`}>
                      <span className="log-time">[{new Date(line.timestampUtc).toLocaleTimeString()}] </span>
                      {line.message}
                    </span>
                  ))}
                </div>
              </div>
            </div>
          )}

          {/* Keeps the backfill column on the right even when no live run occupies the left. */}
          {!isWatching && <div style={{ flex: 1, minWidth: 0 }} />}
          {showBackfill && (
            <BackfillForm
              replicationName={replicationName}
              onQueued={(runIds) => { setShowBackfill(false); setActiveRunId(runIds[0]) }}
              onClose={() => setShowBackfill(false)}
            />
          )}
        </div>
      )}

      <div className="card flush" style={{ display: 'flex', flexDirection: 'column', minHeight: 0 }} data-testid="run-history-table">
        <div className="card-head tight">
          <span className="card-title sm">Run history</span>
          <span className="spacer row" style={{ gap: 6 }}>
            {(['all', 'failed', 'backfills'] as Filter[]).map((f) => (
              <button
                key={f}
                className={`chip ${filter === f ? 'active' : ''}`}
                onClick={() => setFilter(f)}
                data-testid={`run-filter-${f}`}
              >
                {f === 'all' ? 'All' : f === 'failed' ? 'Failed' : 'Backfills'}
              </button>
            ))}
          </span>
          <RefreshCountdown
            label="Runs"
            dataUpdatedAt={dataUpdatedAt}
            // The interval actually in force, not the constant: while a run is being watched the list
            // refreshes every 1.5 seconds, and a countdown ticking down from ten beside it would be
            // describing a schedule the panel is not on.
            intervalMs={interval}
            testId="runs-countdown"
          />
        </div>

        <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 12 }}>
          {/* "Queued", not "Started": this column has always shown the enqueue time, and now there is
              a real start timestamp beside it for the header to have been lying about. */}
          <span>Queued</span><span>Kind</span><span>Mapping</span><span>Segment</span>
          {/* The worker process that ran it. Recorded since the work queue existed and never shown
              until phase 88 — it is what an operator correlates a run against the machine's own
              logs by, and there was no way to get it out of this screen. */}
          <span>PID</span>
          <span>Read</span><span>Written</span><span>Processing</span>
          {/* Where the mapping's position started and where it got to, as times. The positions
              themselves are an LSN or a version — unreadable as an interval, which is the thing
              somebody looking at a run wants to know. */}
          <span>Watermark</span>
          {/* Test-addressable because the status column's alignment is a real assertion, not a
              visual check: the badges below have to start where this header starts. */}
          <span data-testid="run-status-header">Status</span><span />
        </div>

        <div style={{ overflow: 'auto' }}>
          {!runs && <div className="empty">Loading…</div>}
          {runs && visible.length === 0 && (
            <div className="empty">{filter === 'all' ? 'No runs yet.' : 'No runs match this filter.'}</div>
          )}
          {visible.map((r) => {
            return (
            <div key={r.runId} style={{ display: 'contents' }}>
            <div className="grid-row short" style={{ gridTemplateColumns: COLUMNS, gap: 12 }}>
              <span className="dim row" style={{ gap: 5 }}>
                {/* Only a traced run gets the affordance — for nearly every row there is nothing to
                    open, and a disabled chevron on every line would be the whole table advertising a
                    feature it is not using. A failed run's details are a popup now, not this. */}
                {r.timing && (
                  <button
                    type="button"
                    className="btn-link quiet"
                    style={{ padding: 0 }}
                    aria-expanded={expandedRunId === r.runId}
                    title="This pass was traced — stage timings"
                    onClick={() => setExpandedRunId(expandedRunId === r.runId ? null : r.runId)}
                    data-testid={`run-timing-toggle-${r.runId}`}
                  >
                    {expandedRunId === r.runId ? '▾' : '▸'}
                  </button>
                )}
                {clock(r.enqueuedAtUtc)}
              </span>
              <span><RunKindBadge kind={r.runKind} /></span>
              <span>{r.mappingName}</span>
              <span className="faint">{r.segmentLabel ?? '—'}</span>
              {/* Null for a run no worker ever claimed — still queued, or cancelled first — which
                  is a fact about the run rather than a missing reading. */}
              <span className="mono sm dim" data-testid={`run-pid-${r.runId}`}>{r.pid ?? '—'}</span>
              <span>{r.rowsRead.toLocaleString()}</span>
              <span>{r.rowsWritten.toLocaleString()}</span>
              <span className="dim" title={queueTime(r) ?? undefined}>{processingTime(r)}</span>
              <WatermarkCell run={r} times={watermarkTimes?.[r.runId]} />
              {/* Every status opens the details, not only a failed one — see RunDetailsDialog for
                  why the asymmetry went rather than being levelled the other way.

                  Levelling the markup is not by itself what fixes the column. A button carries the
                  UA stylesheet's `text-align: center` where a span inherits the row's `left`, which
                  is the whole cause of the reported defect — and making every status a button would
                  agree the badges with *each other* while leaving the column centred under a
                  left-aligned `Status` header. `.status-button` sets `text-align: left`, which is
                  what actually puts them back; `justify-self: start` then shrinks the button to its
                  badge, so the thing that looks clickable is the thing that is. */}
              <button
                type="button"
                className="status-button"
                style={{ justifySelf: 'start' }}
                title="Rows, timings, watermark — and the error, if it failed"
                onClick={() => setDetailsRun(r)}
                data-testid={`run-status-${r.runId}`}
              >
                <StatusBadge status={r.status} />
              </button>
              {/* Offered, not performed. A full reload of a table that fell behind can be hours of
                  work, so a pass failing because its source dropped the history it needed reports
                  that and puts the fix one click away — rather than starting it unasked. */}
              <span style={{ justifySelf: 'end' }}>
                {r.failureKind === 'PositionExpired' && (
                  <button
                    type="button"
                    className="btn btn-sm"
                    disabled={resync.isPending}
                    title={
                      'The source no longer holds the changes this pass needed. Resync reloads the ' +
                      'table and clears the stored position, so incremental passes can start again.'
                    }
                    onClick={() => resync.mutate(r.runId)}
                    data-testid={`resync-run-${r.runId}`}
                  >
                    Resync
                  </button>
                )}
              </span>
            </div>
            {expandedRunId === r.runId && r.timing && <TimingDetail timing={r.timing} runId={r.runId} />}
            </div>
            )
          })}
        </div>
      </div>

      {detailsRun && (
        <RunDetailsDialog
          run={detailsRun}
          times={watermarkTimes?.[detailsRun.runId]}
          onClose={() => setDetailsRun(null)}
        />
      )}
    </>
  )
}
