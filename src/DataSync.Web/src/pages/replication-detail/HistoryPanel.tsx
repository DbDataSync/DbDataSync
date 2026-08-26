import { ErrorBanner } from '../../components/ErrorBanner'
import { useReplicationHistory } from '../../api/hooks'

export function HistoryPanel({ replicationName }: { replicationName: string }) {
  const { data: commits, isLoading, error } = useReplicationHistory(replicationName)

  return (
    <div className="card">
      <h2>Config History</h2>
      <p className="muted">Every config change is an auto-commit — this is that replication's git log.</p>
      <ErrorBanner error={error} />
      {isLoading && <p className="muted">Loading…</p>}
      {commits && commits.length === 0 && <p className="empty-state">No history yet.</p>}
      {commits && commits.length > 0 && (
        <table data-testid="history-table">
          <thead>
            <tr>
              <th>When</th>
              <th>Author</th>
              <th>Message</th>
              <th>Commit</th>
            </tr>
          </thead>
          <tbody>
            {commits.map((c) => (
              <tr key={c.sha}>
                <td>{new Date(c.whenUtc).toLocaleString()}</td>
                <td>{c.authorName}</td>
                <td>{c.message}</td>
                <td className="muted">{c.sha.slice(0, 8)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
