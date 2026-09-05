import type { ReadIntent } from '../../api/types'

/**
 * What each `ReadIntent` means, in an operator's terms — see phase 100/102.
 *
 * `InitialLoad`'s hint is deliberately worded as "the application must never choose one on its own"
 * bait: it is the phrase the `DefaultReadIntent` setting exists to let an operator assert about the
 * *other* three, not about this one, but stating what a full load actually costs here is what makes
 * that setting's own hint make sense when it is read beside this list.
 */
export const INTENT_INFO: Record<ReadIntent, { label: string; hint: string }> = {
  InitialLoad: {
    label: 'Initial load',
    hint: 'Reloads the table from scratch. Correct for a mapping that has never completed a pass; ' +
      'chosen deliberately here, it is a full reload of however large the table has grown to be.',
  },
  Changes: {
    label: 'Changes',
    hint: 'Ordinary incremental reads from wherever the last pass left off. The steady state every ' +
      'mapping that has ever completed a pass is in.',
  },
  ChangesFromEarliest: {
    label: 'Changes, from earliest',
    hint: 'Catches up from the oldest position the source can still answer for, without a full ' +
      'reload — usually minutes rather than hours. Not every reader can honour this.',
  },
  ChangesFromLatest: {
    label: 'Changes, from latest',
    hint: 'Skips to now: adopts the current position without reading anything that came before it. ' +
      'Deliberate data loss — every change since the last applied position is never replicated.',
  },
}

/** The order these are offered in, wherever more than one is on screen together. */
const ORDER: ReadIntent[] = ['InitialLoad', 'Changes', 'ChangesFromEarliest', 'ChangesFromLatest']

/**
 * What a picker should offer for one reader: `InitialLoad` always (universal since the Bulk Load
 * pipeline retarget — see architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md),
 * plus whichever of `Changes`/`ChangesFromEarliest`/`ChangesFromLatest` the reader itself declares.
 *
 * A reader with nothing to say about any of the three (batch reload, a scripted query) still gets
 * `InitialLoad` back — never an empty list, which would be a picker with no way to submit it.
 */
export function offeredIntents(supported: ReadIntent[] | undefined): ReadIntent[] {
  const declared = new Set(supported ?? [])
  return ORDER.filter((intent) => intent === 'InitialLoad' || declared.has(intent))
}
