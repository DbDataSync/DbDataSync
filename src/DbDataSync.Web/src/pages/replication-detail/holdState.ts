import type { ReadHold } from '../../api/types'

/**
 * Why a mapping is not running right now, collapsed to one answer — see phase 102.
 *
 * Two independent facts can make a table not run: the whole replication can be disabled or paused
 * (phase 64's `Tasks.Paused`, per-replication), and this one mapping can carry its own `ReadHold`
 * (phase 100/101, per-table). Both can be true at once, and `SchedulerService.ShouldRun` already
 * settles which one actually gates dispatch — the replication-wide gate is checked first, and a held
 * mapping is filtered out of what is due only once that gate has already passed. This mirrors that
 * precedence for display, the same way `StatusCard`'s own `StateIndicator` orders "disabled" ahead of
 * "paused" ahead of "running" for one replication: the more fundamental fact is the one worth saying,
 * and a mapping resumed from its own hold while the replication is still paused must not read as
 * running just because its own hold cleared.
 *
 * `'loading'` is phase 134's addition — `ReadHold.Loading`, set while an initial load's Bulk Load is in
 * flight (a position has been captured but is not yet the live watermark). It clears on its own once the
 * batch completes, so unlike `position-expired` it needs no recovery affordance in the dialog — see
 * `MappingReadStateDialog`'s own doc for why that file is otherwise untouched by this phase.
 */
export type HoldState =
  | 'replication-disabled'
  | 'replication-paused'
  | 'position-expired'
  | 'paused'
  | 'loading'
  | 'none'

export function holdStateOf(taskEnabled: boolean, taskPaused: boolean, mappingHold: ReadHold): HoldState {
  if (!taskEnabled) return 'replication-disabled'
  if (taskPaused) return 'replication-paused'
  if (mappingHold === 'PositionExpired') return 'position-expired'
  if (mappingHold === 'Paused') return 'paused'
  if (mappingHold === 'Loading') return 'loading'
  return 'none'
}

export const HOLD_INFO: Record<HoldState, { label: string; dot: string }> = {
  'replication-disabled': { label: 'Not running — replication disabled', dot: 'dot-bad' },
  'replication-paused': { label: 'Not running — replication paused', dot: 'dot-warn' },
  'position-expired': { label: 'Held — position expired, needs recovery', dot: 'dot-bad' },
  paused: { label: 'Held — paused', dot: 'dot-warn' },
  loading: { label: 'Loading — initial load in progress', dot: 'dot-warn' },
  none: { label: '', dot: '' },
}
