import type { RunKind, RunStatus } from '../api/types'

const classByStatus: Record<RunStatus, string> = {
  Succeeded: 'badge-success',
  Failed: 'badge-danger',
  Cancelled: 'badge-warning',
  Running: 'badge-neutral',
  Pending: 'badge-neutral',
  // A real status since the work queue landed: enqueued and visible in history immediately, not yet
  // claimed by a worker.
  Queued: 'badge-neutral',
}

export function StatusBadge({ status }: { status: RunStatus }) {
  return <span className={`badge ${classByStatus[status]}`}>{status}</span>
}

/** Distinguishes a replication's ongoing incremental pass from an on-demand reload — both can be
 * running for the same replication at the same time, which is the whole point of the run model. */
export function RunKindBadge({ kind }: { kind: RunKind }) {
  return <span className={`badge ${kind === 'Backfill' ? 'badge-info' : 'badge-neutral'}`}>{kind}</span>
}
