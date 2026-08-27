import { useEffect, useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { RunKindBadge, StatusBadge } from '../../components/StatusBadge'
import { BackfillForm } from './BackfillForm'
import { useCancelRun, useInvalidateRunHistory, useRunHistory, useTriggerRun } from '../../api/hooks'
import { useRunHub } from '../../api/useRunHub'
import type { TaskRunRecord } from '../../api/types'

/** A command sent down from the chrome's Backfill…/Run Now buttons. */
export interface RunsCommand {
  kind: 'backfill' | 'run'
  nonce: number
}

const COLUMNS = '1.2fr .7fr .9fr .8fr .7fr .8fr .8fr 1fr'
type Filter = 'all' | 'failed' | 'backfills'

/** Real, unlike the mockup's lag and 24-hour counters: both timestamps are recorded. */
function duration(run: TaskRunRecord) {
  if (!run.endedAtUtc) return '—'
  const ms = new Date(run.endedAtUtc).getTime() - new Date(run.startedAtUtc).getTime()
  if (ms < 0) return '—'
  return ms < 1000 ? `${ms}ms` : `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)}s`
}

const clock = (iso: string) => new Date(iso).toLocaleTimeString()

export function RunsPanel({ replicationName, command }: { replicationName: string; command: RunsCommand | null }) {
  const [activeRunId, setActiveRunId] = useState<string | undefined>(undefined)
  const [showBackfill, setShowBackfill] = useState(false)
  const [filter, setFilter] = useState<Filter>('all')

  const isWatching = !!activeRunId
  const { data: runs, error: historyError } = useRunHistory(replicationName, isWatching ? 1500 : undefined)
  const trigger = useTriggerRun(replicationName)
  const cancel = useCancelRun(replicationName)
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
      <ErrorBanner error={historyError ?? trigger.error ?? cancel.error} />

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
          <span>Started</span><span>Kind</span><span>Mapping</span><span>Segment</span>
          <span>Read</span><span>Written</span><span>Duration</span><span>Status</span>
        </div>

        <div style={{ overflow: 'auto' }}>
          {!runs && <div className="empty">Loading…</div>}
          {runs && visible.length === 0 && (
            <div className="empty">{filter === 'all' ? 'No runs yet.' : 'No runs match this filter.'}</div>
          )}
          {visible.map((r) => (
            <div key={r.runId} className="grid-row short" style={{ gridTemplateColumns: COLUMNS, gap: 12 }}>
              <span className="dim">{clock(r.startedAtUtc)}</span>
              <span><RunKindBadge kind={r.runKind} /></span>
              <span>{r.mappingName}</span>
              <span className="faint">{r.segmentLabel ?? '—'}</span>
              <span>{r.rowsRead.toLocaleString()}</span>
              <span>{r.rowsWritten.toLocaleString()}</span>
              <span className="dim">{duration(r)}</span>
              <span title={r.errorSummary ?? undefined}><StatusBadge status={r.status} /></span>
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
