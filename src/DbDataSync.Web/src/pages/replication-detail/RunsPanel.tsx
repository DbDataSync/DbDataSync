import { useEffect, useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { RunErrorDialog } from '../../components/RunErrorDialog'
import { RunKindBadge, StatusBadge } from '../../components/StatusBadge'
import { BackfillForm } from './BackfillForm'
import {
  MONITORING_REFRESH_MS, useCancelRun, useInvalidateRunHistory, useRunHistory,
  useRunWatermarkTimes, useTriggerRun, useResyncRun,
} from '../../api/hooks'
import { RefreshCountdown } from '../../components/RefreshCountdown'
import { ShellActions } from '../../components/ShellActions'
import { useRunHub } from '../../api/useRunHub'
import type { RunTiming, RunWatermarkTimes, TaskRunRecord } from '../../api/types'

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

/**
 * Processing time: how long the run itself took, from when it started to when it ended.
 *
 * Named for the pair of timestamps it is between, rather than "duration" — which is the word that hid
 * the fact that it used to be measured from the enqueue, so that a backlog read as slow passes. Queue
 * time is the other figure, in this cell's tooltip.
 *
 * A run that never started has no processing time, the same as one that has not ended.
 */
function processingTime(run: TaskRunRecord) {
  if (!run.endedAtUtc || !run.startedAtUtc) return '—'
  const ms = new Date(run.endedAtUtc).getTime() - new Date(run.startedAtUtc).getTime()
  if (ms < 0) return '—'
  return elapsed(ms)
}

/**
 * Queue time: how long the run sat queued before it started, for the processing cell's tooltip.
 *
 * Null rather than "0ms" when the wait rounds to nothing: the ordinary case is an idle worker taking
 * the item immediately, and a tooltip on every row saying so would be noise on the rows where the
 * answer is boring, and easy to miss on the rows where it is not.
 */
function queueTime(run: TaskRunRecord): string | null {
  if (!run.startedAtUtc || !run.enqueuedAtUtc) return null
  const ms = new Date(run.startedAtUtc).getTime() - new Date(run.enqueuedAtUtc).getTime()
  if (ms < 1000) return null
  return `Queued ${elapsed(ms)} before this run started — not counted in the processing time.`
}

function elapsed(ms: number) {
  return ms < 1000 ? `${ms}ms` : `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)}s`
}

const clock = (iso: string | null) => (iso ? new Date(iso).toLocaleTimeString() : '—')

export function RunsPanel({ replicationName, command }: { replicationName: string; command: RunsCommand | null }) {
  const [activeRunId, setActiveRunId] = useState<string | undefined>(undefined)
  // Which traced run has its timing open. One at a time: this is read to answer a question about one
  // pass, and several expanded at once would push the rest of the history off the screen.
  const [expandedRunId, setExpandedRunId] = useState<string | null>(null)
  const [showBackfill, setShowBackfill] = useState(false)
  const [filter, setFilter] = useState<Filter>('all')
  // The one run whose failure is open in the popup — see RunErrorDialog. Independent of expandedRunId,
  // which is only ever a traced run's stage timings now.
  const [errorRun, setErrorRun] = useState<TaskRunRecord | null>(null)

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
    <div className="pane">
      <ShellActions>
        <RefreshCountdown
          label="Runs"
          dataUpdatedAt={dataUpdatedAt}
          // The interval actually in force, not the constant: while a run is being watched the list
          // refreshes every 1.5 seconds, and a countdown ticking down from ten beside it would be
          // describing a schedule the panel is not on.
          intervalMs={interval}
          testId="runs-countdown"
        />
      </ShellActions>

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
          <span>Status</span><span />
        </div>

        <div style={{ overflow: 'auto' }}>
          {!runs && <div className="empty">Loading…</div>}
          {runs && visible.length === 0 && (
            <div className="empty">{filter === 'all' ? 'No runs yet.' : 'No runs match this filter.'}</div>
          )}
          {visible.map((r) => {
            const hasError = r.status === 'Failed' && !!r.errorSummary
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
              {hasError ? (
                <button
                  type="button"
                  className="btn-link"
                  style={{ padding: 0 }}
                  title="See why this run failed"
                  onClick={() => setErrorRun(r)}
                  data-testid={`run-status-${r.runId}`}
                >
                  <StatusBadge status={r.status} />
                </button>
              ) : (
                <span><StatusBadge status={r.status} /></span>
              )}
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

      {errorRun && <RunErrorDialog run={errorRun} onClose={() => setErrorRun(null)} />}
    </div>
  )
}

/**
 * Where this pass's watermark started and where it got to, as times — see phase 88.
 *
 * **The stored value is a position, and the position is not the point.** `PreviousWatermark` and
 * `NewWatermark` are a CDC LSN or a Change Tracking version; what an operator reads a run list for
 * is how much time a pass covered, and no amount of staring at `0x0000002A000001B80003` answers
 * that. The times come from `ChangeCheckHistory` — the earliest poll that observed the source at or
 * past each position — and the raw value stays in the tooltip, where it is exactly what somebody
 * comparing this against a query on the source needs.
 *
 * **Three different dashes, and they mean three different things.** A run with no watermarks at all
 * is a backfill, a verification or a failed pass — it made no position durable, and the whole cell
 * is one dash. A watermark whose time did not resolve has aged out of the polling history's
 * retention window, or names a position the source has not been observed at yet; it gets a dash of
 * its own with the raw value still on it. Neither is an error, and neither invents a time.
 */
function WatermarkCell({ run, times }: { run: TaskRunRecord; times: RunWatermarkTimes | undefined }) {
  if (!run.previousWatermark && !run.newWatermark)
    return <span className="faint" data-testid={`run-watermark-${run.runId}`}>—</span>

  return (
    <span className="row" style={{ gap: 4, minWidth: 0 }} data-testid={`run-watermark-${run.runId}`}>
      <WatermarkPoint raw={run.previousWatermark} time={times?.previousWatermarkTimeUtc ?? null} which="from" />
      <span className="faint">→</span>
      <WatermarkPoint raw={run.newWatermark} time={times?.newWatermarkTimeUtc ?? null} which="to" />
    </span>
  )
}

function WatermarkPoint({ raw, time, which }: {
  raw: string | null
  time: string | null
  which: 'from' | 'to'
}) {
  if (!raw) return <span className="faint">—</span>

  const label = which === 'from' ? 'Started from' : 'Advanced to'
  return (
    <span
      className={time ? 'dim' : 'faint'}
      title={
        `${label} source position ${raw}.` +
        (time
          ? ` The source was first observed there at ${new Date(time).toLocaleString()}.`
          : ' No polling history covers that position — it has aged out of the retention window, ' +
            'or the source has not been observed there.')
      }
    >
      {time ? new Date(time).toLocaleTimeString() : '—'}
    </span>
  )
}

/**
 * One traced pass's stage timings, opened beneath its row.
 *
 * **Not seven more columns.** The history table already carries eight, tracing is opt-in and off for
 * nearly every mapping, and seven mostly-empty columns would clutter every row of every replication to
 * serve the rare one — the same row-alignment pressure phase 47 fixed. An untraced run's row is
 * unchanged, down to the pixel.
 *
 * Time to first row is shown as a share of the reader's lifetime rather than only as a number, because
 * that ratio is what the two figures exist to distinguish: a slow first row is the source planning or
 * queueing, and a fast first row with a long lifetime is volume, or a consumer that cannot keep up.
 */
function TimingDetail({ timing, runId }: { timing: RunTiming; runId: string }) {
  return (
    <div className="timing-detail" data-testid={`run-timing-${runId}`}>
      <Stage
        label="Reader"
        kind={timing.readerKind}
        primary={ms(timing.readerLifetimeMs)}
        primaryLabel="lifetime"
        secondary={ms(timing.readerTimeToFirstRowMs)}
        secondaryLabel="to first row"
        note={share(timing.readerTimeToFirstRowMs, timing.readerLifetimeMs)}
      />
      <Stage
        label="Staging"
        kind={timing.stagingKind}
        primary={ms(timing.stagingDurationMs)}
        primaryLabel="duration"
        // The gap between staging and the reader's lifetime is the staging provider's own work beyond
        // consuming the source — a file-based provider uploading what it staged, for instance.
        note={beyondReader(timing)}
      />
      <Stage label="Writer" kind={timing.writerKind} primary={ms(timing.writerDurationMs)} primaryLabel="duration" />
    </div>
  )
}

function Stage({ label, kind, primary, primaryLabel, secondary, secondaryLabel, note }: {
  label: string
  kind: string | null
  primary: string
  primaryLabel: string
  secondary?: string
  secondaryLabel?: string
  note?: string | null
}) {
  return (
    <div className="timing-stage">
      <span className="timing-stage-name">{label}</span>
      <span className="mono sm">{kind ?? '—'}</span>
      <span className="timing-figure">{primary}<span className="faint"> {primaryLabel}</span></span>
      {secondary !== undefined && (
        <span className="timing-figure">{secondary}<span className="faint"> {secondaryLabel}</span></span>
      )}
      {note && <span className="faint">{note}</span>}
    </div>
  )
}

/** "Not measured" and "measured as zero" are different answers, and the column keeps them apart. */
function ms(value: number | null): string {
  if (value === null) return '—'
  if (value < 1000) return `${value}ms`
  return value < 60_000 ? `${(value / 1000).toFixed(1)}s` : `${Math.round(value / 60_000)}m`
}

function share(part: number | null, whole: number | null): string | null {
  if (part === null || whole === null || whole <= 0) return null
  return `${Math.round((part / whole) * 100)}% of the read spent waiting for the first row`
}

function beyondReader(timing: RunTiming): string | null {
  const { stagingDurationMs: staging, readerLifetimeMs: reader } = timing
  if (staging === null || reader === null) return null
  const beyond = staging - reader
  // Under a tick either way is a provider writing straight through as rows arrive, which is the
  // ordinary case and not worth a line of its own.
  return beyond > 50 ? `${ms(beyond)} of it after the source was exhausted` : null
}
