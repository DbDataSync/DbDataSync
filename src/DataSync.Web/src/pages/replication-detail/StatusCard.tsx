import { useReplicationStatus } from '../../api/hooks'

/**
 * What this replication's worker process is doing, right now.
 *
 * **"Not running" is the normal state, and the card says so.** A worker drains its queue and exits, so
 * a replication that is caught up has no process between cycles — which is most of the time for most
 * of them. Left unexplained, an idle healthy replication reads as a broken one, and this card would
 * teach an operator to worry about the wrong thing.
 *
 * Live only. What has been *happening* is the metrics card's question, and it already answers it.
 */
export function StatusCard({ replicationName, enabled }: { replicationName: string; enabled: boolean }) {
  const { data: status } = useReplicationStatus(replicationName)

  return (
    <div className={`card ${enabled ? 'enabled' : 'disabled'}`} data-testid="replication-status-card">
      <div className="card-head">
        <span className="card-title">Replication status</span>
        <span className="status spacer" data-testid="replication-status-state">
          <span className={`dot ${status?.running ? 'dot-ok' : 'dot-idle'}`} />
          {status === undefined ? '…' : status.running ? 'running' : 'not running'}
        </span>
      </div>

      <div className="card-body">
        {status?.running ? (
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
            <Figure label="PID" value={String(status.pid)} />
            <Figure label="Memory" value={formatBytes(status.memoryBytes)} />
            <Figure label="CPU time" value={formatMs(status.cpuMilliseconds)} />
            <Figure label="Started" value={status.startedAtUtc ? formatAgo(status.startedAtUtc) : '—'} />
          </div>
        ) : (
          <span className="hint">
            No worker process. A worker drains its queue and exits, so this is the usual state between
            passes rather than a problem.
          </span>
        )}
      </div>
    </div>
  )
}

function Figure({ label, value }: { label: string; value: string }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
      <span style={{ font: '500 10.5px var(--ui)', color: 'var(--ink-4)', textTransform: 'uppercase', letterSpacing: '.04em' }}>
        {label}
      </span>
      <span style={{ font: '500 13px var(--ui)', color: 'var(--ink)' }}>{value}</span>
    </div>
  )
}

function formatBytes(bytes: number | null) {
  if (bytes === null) return '—'
  const mb = bytes / (1024 * 1024)
  return mb < 1024 ? `${mb.toFixed(0)} MB` : `${(mb / 1024).toFixed(1)} GB`
}

function formatMs(ms: number | null) {
  if (ms === null) return '—'
  if (ms < 1000) return `${Math.round(ms)}ms`
  return ms < 60_000 ? `${(ms / 1000).toFixed(1)}s` : `${Math.round(ms / 60_000)}m`
}

function formatAgo(iso: string) {
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000)
  if (seconds < 90) return `${Math.round(seconds)}s ago`
  return seconds < 5400 ? `${Math.round(seconds / 60)}m ago` : `${Math.round(seconds / 3600)}h ago`
}
