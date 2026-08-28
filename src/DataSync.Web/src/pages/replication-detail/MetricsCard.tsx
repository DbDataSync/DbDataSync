import { useState } from 'react'
import { useRunMetrics } from '../../api/hooks'
import type { MetricsWindow, RunMetricsBucket } from '../../api/types'

const WINDOWS: { id: MetricsWindow; label: string }[] = [
  { id: '1h', label: '1h' },
  { id: '24h', label: '24h' },
  { id: '7d', label: '7d' },
]

/**
 * What this replication's incremental passes actually did, over a window the operator picks.
 *
 * Every figure comes from `TaskRuns`, which has recorded them since phase 5 — the gap this closes is
 * that nobody had written the query. Real numbers or nothing: a card that shows a dash where it means
 * zero is inventing a reading, which is why phase 15 shipped none of this rather than shipping
 * placeholders.
 *
 * **Primary passes only.** A backfill moving ten million rows next to incremental passes moving
 * hundreds dominates every total it is added to, so it is a different question and gets a different
 * answer. The endpoint takes the kind; this card asks the one an operator means by "is it working".
 */
export function MetricsCard({ replicationName }: { replicationName: string }) {
  const [window, setWindow] = useState<MetricsWindow>('24h')
  const { data, isLoading } = useRunMetrics(replicationName, window)

  return (
    <div className="card" data-testid="metrics-card">
      <div className="card-head">
        <span className="card-title">Last {window}</span>
        <span className="spacer row" style={{ gap: 4 }}>
          {WINDOWS.map((w) => (
            <button
              key={w.id}
              type="button"
              className={`btn btn-sm ${window === w.id ? 'btn-primary' : ''}`}
              onClick={() => setWindow(w.id)}
              data-testid={`metrics-window-${w.id}`}
            >
              {w.label}
            </button>
          ))}
        </span>
      </div>

      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        {isLoading && <span className="hint">Loading…</span>}

        {data && (
          <>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
              <Figure label="Passes" value={data.runs.toLocaleString()} testId="metrics-runs" />
              <Figure
                label="Failed"
                value={data.failures.toLocaleString()}
                tone={data.failures > 0 ? 'bad' : undefined}
                testId="metrics-failures"
              />
              <Figure label="Rows written" value={data.rowsWritten.toLocaleString()} testId="metrics-rows-written" />
              <Figure label="Rows read" value={data.rowsRead.toLocaleString()} testId="metrics-rows-read" />
            </div>

            <div className="divider" />

            {/* A distribution, not a single figure. One number reads like a target the system is
                measuring itself against; it is not, it is what happened. */}
            <Figure
              label="Pass duration"
              value={data.durationP50Ms === null
                ? 'no completed pass'
                : `p50 ${formatMs(data.durationP50Ms)} · p95 ${formatMs(data.durationP95Ms!)} · max ${formatMs(data.durationMaxMs!)}`}
              testId="metrics-duration"
            />

            <Sparkline buckets={data.buckets} />

            <div className="divider" />

            {/* Not called "lag", and that matters: a replication that ran two minutes ago and found
                nothing looks identical to one that ran two minutes ago and is an hour behind. This
                says when a pass last completed, which is a different and answerable question. */}
            <Figure
              label="Last completed pass"
              value={data.lastCompletedPassUtc === null ? 'never' : `${formatAgo(data.lastCompletedPassUtc)} ago`}
              testId="metrics-last-pass"
            />
          </>
        )}
      </div>
    </div>
  )
}

function Figure({ label, value, tone, testId }: {
  label: string
  value: string
  tone?: 'bad'
  testId: string
}) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }} data-testid={testId}>
      <span style={{ font: '500 10.5px var(--ui)', color: 'var(--ink-4)', textTransform: 'uppercase', letterSpacing: '.04em' }}>
        {label}
      </span>
      <span style={{ font: '500 13px var(--ui)', color: tone === 'bad' ? 'var(--danger-ink)' : 'var(--ink)' }}>
        {value}
      </span>
    </div>
  )
}

/**
 * Runs per bucket, as bars. Deliberately not a charting library: it is a row of divs whose heights are
 * a ratio, and a dependency for that would be a dependency to keep.
 */
function Sparkline({ buckets }: { buckets: RunMetricsBucket[] }) {
  const peak = Math.max(1, ...buckets.map((b) => b.runs))

  return (
    <div
      style={{ display: 'flex', alignItems: 'flex-end', gap: 1, height: 32 }}
      data-testid="metrics-sparkline"
      aria-label={`${buckets.reduce((sum, b) => sum + b.runs, 0)} passes across ${buckets.length} intervals`}
    >
      {buckets.map((bucket, i) => (
        <div
          key={i}
          title={`${bucket.runs} pass(es), ${bucket.rowsWritten.toLocaleString()} row(s) written`}
          style={{
            flex: 1,
            // A floor of 1px so an empty interval is visibly empty rather than absent — a gap in a
            // row of bars reads as missing data, which is not what "nothing ran" means.
            height: `${Math.max(1, (bucket.runs / peak) * 32)}px`,
            background: bucket.failures > 0 ? 'var(--danger)' : 'var(--accent)',
            opacity: bucket.runs === 0 ? 0.18 : 1,
            borderRadius: 1,
          }}
        />
      ))}
    </div>
  )
}

function formatMs(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)}ms`
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`
  return `${Math.round(ms / 60_000)}m`
}

function formatAgo(iso: string): string {
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000)
  if (seconds < 90) return `${Math.round(seconds)}s`
  if (seconds < 5400) return `${Math.round(seconds / 60)}m`
  if (seconds < 172_800) return `${Math.round(seconds / 3600)}h`
  return `${Math.round(seconds / 86_400)}d`
}
