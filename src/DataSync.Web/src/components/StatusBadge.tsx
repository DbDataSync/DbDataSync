import type { RunKind, RunStatus } from '../api/types'

/** The design shows run state as a coloured dot plus a lowercase word, not a filled pill. */
const dotByStatus: Record<RunStatus, string> = {
  Succeeded: 'dot-ok',
  Failed: 'dot-bad',
  Cancelled: 'dot-warn',
  Running: 'dot-ok',
  Pending: 'dot-idle',
  Queued: 'dot-idle',
}

export function StatusBadge({ status }: { status: RunStatus }) {
  return (
    <span className="status">
      <span className={`dot ${dotByStatus[status]}`} />
      {status.toLowerCase()}
    </span>
  )
}

/** Primary and Backfill get distinct chips in the design — grey and violet respectively. */
export function RunKindBadge({ kind }: { kind: RunKind }) {
  return <span className={`badge ${kind === 'Backfill' ? 'badge-backfill' : 'badge-primary'}`}>{kind.toUpperCase()}</span>
}
