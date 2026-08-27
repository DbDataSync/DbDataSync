import { ErrorBanner } from '../../components/ErrorBanner'
import { useReplicationHistory } from '../../api/hooks'

const COLUMNS = '1.2fr .9fr 3fr .7fr'

/** "Version Control" in the design; the same auto-commit log it has always been. The mockup's
 * Revert action and "Diff vs production" are omitted — neither has an endpoint. */
export function HistoryPanel({ replicationName }: { replicationName: string }) {
  const { data: commits, isLoading, error } = useReplicationHistory(replicationName)

  return (
    <div className="pane">
      <div className="page-head">
        <span className="page-title">Config history</span>
        <span className="page-note">Every config change is an auto-commit. This is the replication's git log.</span>
      </div>

      <ErrorBanner error={error} />

      <div className="card flush" data-testid="history-table">
        <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
          <span>When</span><span>Author</span><span>Message</span><span>Commit</span>
        </div>
        {isLoading && <div className="empty">Loading…</div>}
        {commits?.length === 0 && <div className="empty">No history yet.</div>}
        {(commits ?? []).map((c) => (
          <div key={c.sha} className="grid-row short" style={{ gridTemplateColumns: COLUMNS, gap: 14 }} data-testid="history-row">
            <span className="dim">{new Date(c.whenUtc).toLocaleString()}</span>
            <span className="dim" style={{ fontFamily: 'var(--ui)' }}>{c.authorName}</span>
            <span style={{ fontFamily: 'var(--ui)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{c.message}</span>
            <span style={{ color: 'var(--accent)' }}>{c.sha.slice(0, 8)}</span>
          </div>
        ))}
      </div>
    </div>
  )
}
