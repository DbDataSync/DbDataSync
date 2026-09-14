import { ErrorBanner } from '../../components/ErrorBanner'
import { useReplicationPauseHistory } from '../../api/hooks'

const COLUMNS = '1.3fr .9fr .7fr 2fr .9fr'

/**
 * Every pause and resume this replication has recorded, over both grains — see phase 131.
 *
 * Deliberately simpler than `RunsPanel`: this is a low-volume audit list of human pause actions, not
 * a live-polled operational view. A flat table, a plain `GET` with no live hub subscription, and one
 * `limit` matching the endpoint's own default — the same posture `HistoryPanel` beside it already
 * takes for its own low-traffic history screen, not `RunsPanel`'s countdown-and-cursor machinery.
 *
 * No live refresh countdown: a screen of past events does not go stale the way lag or a running pass
 * does, so a manual reload is enough — again matching `HistoryPanel`, not the Monitoring tabs beside
 * this one.
 */
export function PauseHistoryPanel({ replicationName }: { replicationName: string }) {
  const { data: events, isLoading, error } = useReplicationPauseHistory(replicationName)

  return (
    <div className="card flush" data-testid="pause-history-table">
      <ErrorBanner error={error} />

      <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
        <span>When</span><span>Scope</span><span>Action</span><span>Note</span><span>By</span>
      </div>
      {isLoading && <div className="empty">Loading…</div>}
      {events?.length === 0 && <div className="empty">No pauses or resumes recorded yet.</div>}
      {(events ?? []).map((e) => (
        <div
          key={e.id}
          className="grid-row short"
          style={{ gridTemplateColumns: COLUMNS, gap: 14 }}
          data-testid="pause-history-row"
        >
          <span className="dim">{new Date(e.performedAtUtc).toLocaleString()}</span>
          <span className={e.mappingName ? 'mono sm' : undefined}>{e.mappingName ?? 'Replication'}</span>
          <span className={`badge ${e.action === 'Paused' ? 'badge-primary' : 'badge-accent'}`}>
            {e.action}
          </span>
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {e.note || <span className="faint">—</span>}
          </span>
          <span className="dim">{e.performedBy}</span>
        </div>
      ))}
    </div>
  )
}
