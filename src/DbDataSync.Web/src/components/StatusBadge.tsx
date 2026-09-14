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

const badgeClassByKind: Record<RunKind, string> = {
  Primary: 'badge-primary',
  BulkLoad: 'badge-bulk-load',
  // Phase 124: its own chip, distinct from BulkLoad — a delete-diff sweep is on-demand,
  // non-incremental work like a BulkLoad, but a different action an operator should be able to
  // tell apart in run history at a glance.
  ReconcileDeletes: 'badge-reconcile',
}

export function RunKindBadge({ kind }: { kind: RunKind }) {
  return <span className={`badge ${badgeClassByKind[kind]}`}>{kind.toUpperCase()}</span>
}
