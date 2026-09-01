import { useEffect, useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { RunKindBadge, StatusBadge } from '../../components/StatusBadge'
import { BackfillForm } from './BackfillForm'
import { useCancelRun, useInvalidateRunHistory, useRunHistory, useTriggerRun, useResyncRun } from '../../api/hooks'
import { useRunHub } from '../../api/useRunHub'
import type { RunTiming, TaskRunRecord } from '../../api/types'

/** A command sent down from the chrome's Backfill…/Run Now buttons. */
export interface RunsCommand {
  kind: 'backfill' | 'run'
  nonce: number
}

const COLUMNS = '1.2fr .7fr .9fr .8fr .6fr .7fr .7fr 1fr 78px'
type Filter = 'all' | 'failed' | 'backfills'

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

  const isWatching = !!activeRunId
  const { data: runs, error: historyError } = useRunHistory(replicationName, isWatching ? 1500 : undefined)
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
          <span>Read</span><span>Written</span><span>Processing</span><span>Status</span><span />
        </div>

        <div style={{ overflow: 'auto' }}>
          {!runs && <div className="empty">Loading…</div>}
          {runs && visible.length === 0 && (
            <div className="empty">{filter === 'all' ? 'No runs yet.' : 'No runs match this filter.'}</div>
          )}
          {visible.map((r) => {
            const hasError = r.status === 'Failed' && !!r.errorSummary
            const expandable = !!r.timing || hasError
            return (
            <div key={r.runId} style={{ display: 'contents' }}>
            <div className="grid-row short" style={{ gridTemplateColumns: COLUMNS, gap: 12 }}>
              <span className="dim row" style={{ gap: 5 }}>
                {/* Only a traced or failed run gets the affordance — for nearly every row there is
                    nothing to open, and a disabled chevron on every line would be the whole table
                    advertising a feature it is not using. */}
                {expandable && (
                  <button
                    type="button"
                    className="btn-link quiet"
                    style={{ padding: 0 }}
                    aria-expanded={expandedRunId === r.runId}
                    title={r.timing ? 'This pass was traced — stage timings' : 'This pass failed — error details'}
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
              <span>{r.rowsRead.toLocaleString()}</span>
              <span>{r.rowsWritten.toLocaleString()}</span>
              <span className="dim" title={queueTime(r) ?? undefined}>{processingTime(r)}</span>
              <span title={r.errorSummary ?? undefined}><StatusBadge status={r.status} /></span>
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
            {expandedRunId === r.runId && hasError && (
              <div className="run-error-detail" data-testid={`run-error-${r.runId}`}>{r.errorSummary}</div>
            )}
            </div>
            )
          })}
        </div>
      </div>
    </div>
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
