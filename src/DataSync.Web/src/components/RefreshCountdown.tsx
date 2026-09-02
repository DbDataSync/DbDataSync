import { useEffect, useState } from 'react'
import { MONITORING_REFRESH_MS } from '../api/hooks'

/**
 * Seconds until a polled query refetches, from the moment it last succeeded — see phase 88.
 *
 * **Driven by the query's own `dataUpdatedAt`, not by a timer this hook starts.** That is what makes
 * the countdown honest rather than decorative: react-query restarts its interval from the moment
 * data lands, so anything that fetches early — a manual invalidation, a window refocus, a run
 * completing on the hub — moves the real next refresh, and a countdown keeping its own schedule
 * would drift away from it within a minute and then lie for as long as the tab is open. Reading the
 * timestamp means the display cannot disagree with the thing it describes.
 *
 * It also means each panel's countdown is its own. Three panels mount at three different moments and
 * each has its own `dataUpdatedAt`, so the three numbers differ — which is correct, and the reason
 * this is a hook per panel rather than one synchronised clock in the shell.
 *
 * Null before the first successful fetch: there is no interval running yet, so there is nothing to
 * count down to.
 */
function useRefreshCountdown(
  dataUpdatedAt: number | undefined, intervalMs: number = MONITORING_REFRESH_MS,
): number | null {
  // Half a second, for a display whose smallest unit is one. Fast enough that the number never
  // visibly sticks, slow enough that a ticking span in the chrome is not re-rendering on every
  // frame of every screen it appears on.
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 500)
    return () => clearInterval(timer)
  }, [])

  if (!dataUpdatedAt) return null

  // Clamped at zero rather than allowed to go negative: a fetch in flight, or a tab the browser has
  // throttled, leaves the deadline in the past, and "0s" is what that means.
  const remaining = dataUpdatedAt + intervalMs - now
  return Math.max(0, Math.min(Math.ceil(intervalMs / 1000), Math.ceil(remaining / 1000)))
}

/**
 * When the panel beside this refreshes itself, in the shell's action bar.
 *
 * **Labelled, because there are up to three of them at once.** The Runs tab shows its own and the
 * metrics card's; the Monitoring tab shows its own and the metrics card's. Two bare numbers counting
 * down at different rates would be a puzzle rather than an answer, so each says what it is counting
 * for.
 *
 * The ring is the same figure as the number and exists for the reading nobody stops for: a glance at
 * the chrome says roughly how fresh the screen is without anybody parsing a digit.
 */
export function RefreshCountdown({ label, dataUpdatedAt, intervalMs = MONITORING_REFRESH_MS, testId }: {
  label: string
  dataUpdatedAt: number | undefined
  intervalMs?: number
  testId: string
}) {
  const seconds = useRefreshCountdown(dataUpdatedAt, intervalMs)
  const total = Math.round(intervalMs / 1000)

  return (
    <span
      className="refresh-countdown"
      data-testid={testId}
      // The number as an attribute as well as as text, so a test can assert the cadence rather than
      // scrape a string that also carries a label — the same instinct as phase 86's `data-lag-state`.
      data-seconds={seconds ?? ''}
      title={
        seconds === null
          ? `${label} has not loaded yet.`
          : `${label} refreshes every ${total} seconds — next in ${seconds}.`
      }
    >
      <Ring remaining={seconds} total={total} />
      <span className="refresh-countdown-label">{label}</span>
      <span className="refresh-countdown-value mono">{seconds === null ? '—' : `${seconds}s`}</span>
    </span>
  )
}

/**
 * A 12px ring that empties as the interval runs down. An inline SVG rather than a dependency, for
 * the same reason `MetricsCard`'s sparkline is a row of divs: it is one circle and an arithmetic
 * dash offset, and a charting library for that would be a library to keep.
 */
function Ring({ remaining, total }: { remaining: number | null; total: number }) {
  const radius = 5
  const circumference = 2 * Math.PI * radius
  const fraction = remaining === null ? 0 : Math.max(0, Math.min(1, remaining / total))

  return (
    <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true">
      <circle cx="6" cy="6" r={radius} fill="none" stroke="var(--chrome-edge)" strokeWidth="1.5" />
      <circle
        cx="6"
        cy="6"
        r={radius}
        fill="none"
        stroke="var(--accent)"
        strokeWidth="1.5"
        strokeLinecap="round"
        strokeDasharray={circumference}
        strokeDashoffset={circumference * (1 - fraction)}
        // From the top, clockwise — the direction a clock hand goes, so the ring emptying reads as
        // time passing rather than as a progress bar filling.
        transform="rotate(-90 6 6)"
      />
    </svg>
  )
}
