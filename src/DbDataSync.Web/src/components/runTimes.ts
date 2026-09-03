import type { TaskRunRecord } from '../api/types'

/**
 * How long a run took, in the two figures the product keeps apart — its own work, and the wait before
 * it. Plain functions in their own module rather than beside the components that render them, so a
 * file of components stays a file of components (fast refresh) and both the history row and the
 * details popup can state the same numbers the same way.
 */

/**
 * Processing time: how long the run itself took, from when it started to when it ended.
 *
 * Named for the pair of timestamps it is between, rather than "duration" — which is the word that hid
 * the fact that it used to be measured from the enqueue, so that a backlog read as slow passes. Queue
 * time is the other figure, in this cell's tooltip.
 *
 * A run that never started has no processing time, the same as one that has not ended.
 */
export function processingTime(run: TaskRunRecord) {
  if (!run.endedAtUtc || !run.startedAtUtc) return '—'
  const ms = new Date(run.endedAtUtc).getTime() - new Date(run.startedAtUtc).getTime()
  if (ms < 0) return '—'
  return elapsed(ms)
}

/**
 * Queue time: how long the run sat queued before it started, for the processing cell's tooltip.
 *
 * Null rather than "0ms" when the wait rounds to nothing: the ordinary case is an idle worker taking
 * the item immediately, and a tooltip on every row saying so would be noise on the rows where the
 * answer is boring, and easy to miss on the rows where it is not.
 */
export function queueTime(run: TaskRunRecord): string | null {
  if (!run.startedAtUtc || !run.enqueuedAtUtc) return null
  const ms = new Date(run.startedAtUtc).getTime() - new Date(run.enqueuedAtUtc).getTime()
  if (ms < 1000) return null
  return `Queued ${elapsed(ms)} before this run started — not counted in the processing time.`
}

function elapsed(ms: number) {
  return ms < 1000 ? `${ms}ms` : `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)}s`
}
