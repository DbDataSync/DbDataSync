import { useEffect, useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { StatusBadge } from '../../components/StatusBadge'
import { useCancelRun, useInvalidateRunHistory, useRunHistory, useTriggerRun } from '../../api/hooks'
import { useRunHub } from '../../api/useRunHub'

export function RunsPanel({ replicationName }: { replicationName: string }) {
  const [activeRunId, setActiveRunId] = useState<string | undefined>(undefined)
  const isWatching = !!activeRunId
  const { data: runs, error: historyError } = useRunHistory(replicationName, isWatching ? 1500 : undefined)
  const trigger = useTriggerRun(replicationName)
  const cancel = useCancelRun(replicationName)
  const { logLines, completed } = useRunHub(activeRunId)
  const invalidateRunHistory = useInvalidateRunHistory(replicationName)

  useEffect(() => {
    if (completed) {
      // Force a fresh history fetch now that the final TaskRuns row is known to exist, rather than
      // waiting on the interval poll below to eventually land on one (see useInvalidateRunHistory).
      invalidateRunHistory()
      const timeout = setTimeout(() => setActiveRunId(undefined), 3000)
      return () => clearTimeout(timeout)
    }
  }, [completed, invalidateRunHistory])

  const onTrigger = async () => {
    const result = await trigger.mutateAsync()
    // A trigger now enqueues one Primary pass per table mapping, so this can return several RunIds —
    // this panel still only live-watches one at a time (the first), matching how single-table-mapping
    // replications behave today. Watching every mapping's own run live is future SPA work (see
    // architecture/implementation/phase-9-work-queue-schema.md); the full history table below already
    // reflects every mapping's runs regardless.
    setActiveRunId(result.runIds[0])
  }

  return (
    <div className="card">
      <div className="row-between">
        <h2>Runs</h2>
        <button className="btn btn-primary" onClick={onTrigger} disabled={trigger.isPending || isWatching} data-testid="trigger-run-button">
          {trigger.isPending ? 'Starting…' : 'Run Now'}
        </button>
      </div>

      <ErrorBanner error={historyError ?? trigger.error ?? cancel.error} />

      {isWatching && (
        <div className="stack" style={{ marginBottom: 16 }} data-testid="live-run-panel">
          <div className="row-between">
            <div className="row">
              <strong>Live run</strong>
              {completed ? <StatusBadge status={completed.status} /> : <span className="badge badge-neutral">Running</span>}
            </div>
            {!completed && (
              <button className="btn btn-sm btn-danger" onClick={() => cancel.mutate(activeRunId!)}>
                Cancel
              </button>
            )}
          </div>
          <div className="log-viewer" data-testid="live-log-viewer">
            {logLines.length === 0 && <div className="muted">Waiting for log output…</div>}
            {logLines.map((line) => (
              <div key={line.id} className={`log-line level-${line.level}`}>
                [{new Date(line.timestampUtc).toLocaleTimeString()}] {line.message}
              </div>
            ))}
          </div>
          {completed && (
            <p className="muted">
              {completed.rowsRead} row(s) read, {completed.rowsWritten} row(s) written.
              {completed.errorSummary && <span> — {completed.errorSummary}</span>}
            </p>
          )}
        </div>
      )}

      {runs && runs.length === 0 && <p className="empty-state">No runs yet.</p>}
      {runs && runs.length > 0 && (
        <table data-testid="run-history-table">
          <thead>
            <tr>
              <th>Started</th>
              <th>Status</th>
              <th>Rows Read</th>
              <th>Rows Written</th>
              <th>Error</th>
            </tr>
          </thead>
          <tbody>
            {runs.map((r) => (
              <tr key={r.runId}>
                <td>{new Date(r.startedAtUtc).toLocaleString()}</td>
                <td>
                  <StatusBadge status={r.status} />
                </td>
                <td>{r.rowsRead}</td>
                <td>{r.rowsWritten}</td>
                <td className="muted">{r.errorSummary ?? '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
