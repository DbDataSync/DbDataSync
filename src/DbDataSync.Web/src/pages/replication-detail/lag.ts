import type { MappingLag } from '../../api/types'

/**
 * The four things a mapping's lag can be, kept apart — see phase 86.
 *
 * A single "—" for the last three would be the whole point missed: "this mechanism cannot report
 * lag", "it can and has not yet", and "it is zero" are three different facts about a replication,
 * and only one of them is reassuring. The state is rendered onto each row as a data attribute as
 * well as through its styling, so the distinction is assertable rather than only visible.
 */
export type LagState = 'exact' | 'estimated' | 'not-applicable' | 'no-data'

export function lagStateOf(lag: MappingLag | undefined): LagState {
  if (!lag || !lag.supported) return 'not-applicable'
  if (lag.exactLagMs !== null) return 'exact'
  if (lag.estimatedLagMs !== null) return 'estimated'
  return 'no-data'
}

/**
 * A duration, at the coarseness the figure deserves.
 *
 * Not `MetricsCard`'s `formatMs`, which tops out at minutes because a pass taking longer than an
 * hour is the exception there. A lag of hours is the case this whole screen exists for, and "212m"
 * is a number somebody has to divide before they know whether to care.
 */
export function formatLag(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)}ms`
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`
  // The boundaries are the unit's own, not `formatAgo`'s 90-minute and 48-hour overhangs. Those
  // exist to keep "an hour and a half ago" from reading as a round number it isn't; a lag is a
  // figure somebody is deciding on, and "60m" makes them do the division that decides it.
  if (ms < 3_600_000) return `${Math.round(ms / 60_000)}m`
  if (ms < 86_400_000) return `${trim(ms / 3_600_000)}h`
  return `${trim(ms / 86_400_000)}d`
}

/** One decimal where it says something, none where it says ".0" — an hour behind is "1h". */
const trim = (value: number) => value.toFixed(1).replace(/\.0$/, '')
