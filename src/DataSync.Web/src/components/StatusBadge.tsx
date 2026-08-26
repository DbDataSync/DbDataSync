import type { RunStatus } from '../api/types'

const classByStatus: Record<RunStatus, string> = {
  Succeeded: 'badge-success',
  Failed: 'badge-danger',
  Cancelled: 'badge-warning',
  Running: 'badge-neutral',
  Pending: 'badge-neutral',
}

export function StatusBadge({ status }: { status: RunStatus }) {
  return <span className={`badge ${classByStatus[status]}`}>{status}</span>
}
