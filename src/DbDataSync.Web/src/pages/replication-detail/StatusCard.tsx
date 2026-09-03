import { useReplicationStatus } from '../../api/hooks'
import { DisabledIcon, PauseIcon, PulseIcon } from '../../components/icons'
import type { ReplicationStatus } from '../../api/types'

/**
 * What this replication's worker process is doing, right now.
 *
 * **"Idle" is a normal state, and the card says so.** A continuous worker stays up between
 * passes and leaves once it has gone its idle timeout without finding a single changed row — so a
 * replication under load is running, and one that has been quiet for a minute is not. Left
 * unexplained, an idle healthy replication reads as a broken one, and this card would teach an
 * operator to worry about the wrong thing.
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
          {status === undefined ? '…' : <StateIndicator enabled={enabled} status={status} />}
        </span>
      </div>

      <div className="card-body">
        {/* A hold is the reason a healthy-looking replication is doing nothing, so it is said before
            the process figures rather than after them — and with the note, because "paused" without
            "why" sends whoever finds it looking for somebody to ask. The history of pauses is
            deliberately not here: see architecture/planning/todo/pause-history-ui.md. */}
        {status?.paused && (
          <div className="banner warn" role="status" data-testid="replication-paused-notice">
            <div><strong>Paused.</strong> Nothing new will be scheduled until it is resumed.</div>
            {status.pauseNote && (
              <div style={{ marginTop: 4 }} data-testid="replication-pause-note">{status.pauseNote}</div>
            )}
          </div>
        )}

        {status?.running ? (
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
            <Figure label="PID" value={String(status.pid)} />
            <Figure label="Memory" value={formatBytes(status.memoryBytes)} />
            <Figure label="CPU time" value={formatMs(status.cpuMilliseconds)} />
            <Figure label="Started" value={status.startedAtUtc ? formatAgo(status.startedAtUtc) : '—'} />
          </div>
        ) : (
          <span className="hint">
            No worker process. A worker stays up between passes and leaves once it has gone its idle
            timeout without finding a changed row — so this is a quiet replication, not a broken one.
          </span>
        )}
      </div>
    </div>
  )
}

/**
 * One indicator, four states, three icons.
 *
 * The order matters: a disabled replication can also be paused, and disabled is the more fundamental
 * fact — a hold on something that was never going to run anyway is not the thing to report. Running
 * and Idle share the pulse glyph and differ only in colour and word, because they are the same
 * healthy process seen at two moments; Disabled and Paused are genuinely other situations and look it.
 *
 * Before this, all four read as "not running" — a broken-looking sentence for three states that are
 * nothing like each other.
 */
function StateIndicator({ enabled, status }: { enabled: boolean; status: ReplicationStatus }) {
  const { icon, color, label } =
    !enabled ? { icon: <DisabledIcon />, color: 'var(--bad)', label: 'Disabled' }
    : status.paused ? { icon: <PauseIcon />, color: 'var(--warn)', label: 'Paused' }
    : status.running ? { icon: <PulseIcon />, color: 'var(--ok)', label: 'Running' }
    : { icon: <PulseIcon />, color: 'var(--idle)', label: 'Idle' }

  return (
    <>
      <span style={{ color, display: 'inline-flex' }} aria-hidden="true">{icon}</span>
      {label}
    </>
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
