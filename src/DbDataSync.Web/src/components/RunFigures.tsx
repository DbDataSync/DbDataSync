import type { RunTiming, RunWatermarkTimes, TaskRunRecord } from '../api/types'

/**
 * The figures a run is read for — how long it took, where its watermark moved, what each stage of the
 * pipeline spent — and the pieces that render them.
 *
 * Here rather than in `RunsPanel`, where all of this used to live, because the run-details popup shows
 * the same figures the history row does and has to render them the same way: two copies would drift,
 * and importing them back out of `RunsPanel` would make a cycle, since the panel is what opens the
 * popup. Nothing here is new — it is the panel's own code, moved verbatim.
 */

/**
 * Where this pass's watermark started and where it got to, as times — see phase 88.
 *
 * **The stored value is a position, and the position is not the point.** `PreviousWatermark` and
 * `NewWatermark` are a CDC LSN or a Change Tracking version; what an operator reads a run list for
 * is how much time a pass covered, and no amount of staring at `0x0000002A000001B80003` answers
 * that. The times come from `ChangeCheckHistory` — the earliest poll that observed the source at or
 * past each position — and the raw value stays in the tooltip, where it is exactly what somebody
 * comparing this against a query on the source needs.
 *
 * **Three different dashes, and they mean three different things.** A run with no watermarks at all
 * is a backfill, a verification or a failed pass — it made no position durable, and the whole cell
 * is one dash. A watermark whose time did not resolve has aged out of the polling history's
 * retention window, or names a position the source has not been observed at yet; it gets a dash of
 * its own with the raw value still on it. Neither is an error, and neither invents a time.
 */
export function WatermarkCell({ run, times }: { run: TaskRunRecord; times: RunWatermarkTimes | undefined }) {
  if (!run.previousWatermark && !run.newWatermark)
    return <span className="faint" data-testid={`run-watermark-${run.runId}`}>—</span>

  return (
    <span className="row" style={{ gap: 4, minWidth: 0 }} data-testid={`run-watermark-${run.runId}`}>
      <WatermarkPoint raw={run.previousWatermark} time={times?.previousWatermarkTimeUtc ?? null} which="from" />
      <span className="faint">→</span>
      <WatermarkPoint raw={run.newWatermark} time={times?.newWatermarkTimeUtc ?? null} which="to" />
    </span>
  )
}

function WatermarkPoint({ raw, time, which }: {
  raw: string | null
  time: string | null
  which: 'from' | 'to'
}) {
  if (!raw) return <span className="faint">—</span>

  const label = which === 'from' ? 'Started from' : 'Advanced to'
  return (
    <span
      className={time ? 'dim' : 'faint'}
      title={
        `${label} source position ${raw}.` +
        (time
          ? ` The source was first observed there at ${new Date(time).toLocaleString()}.`
          : ' No polling history covers that position — it has aged out of the retention window, ' +
            'or the source has not been observed there.')
      }
    >
      {time ? new Date(time).toLocaleTimeString() : '—'}
    </span>
  )
}

/**
 * One traced pass's stage timings, opened beneath its row.
 *
 * **Not seven more columns.** The history table already carries eight, tracing is opt-in and off for
 * nearly every mapping, and seven mostly-empty columns would clutter every row of every replication to
 * serve the rare one — the same row-alignment pressure phase 47 fixed. An untraced run's row is
 * unchanged, down to the pixel.
 *
 * Time to first row is shown as a share of the reader's lifetime rather than only as a number, because
 * that ratio is what the two figures exist to distinguish: a slow first row is the source planning or
 * queueing, and a fast first row with a long lifetime is volume, or a consumer that cannot keep up.
 */
export function TimingDetail({ timing, runId }: { timing: RunTiming; runId: string }) {
  return (
    <div className="timing-detail" data-testid={`run-timing-${runId}`}>
      <Stage
        label="Reader"
        kind={timing.readerKind}
        primary={ms(timing.readerLifetimeMs)}
        primaryLabel="lifetime"
        secondary={ms(timing.readerTimeToFirstRowMs)}
        secondaryLabel="to first row"
        note={share(timing.readerTimeToFirstRowMs, timing.readerLifetimeMs)}
      />
      <Stage
        label="Staging"
        kind={timing.stagingKind}
        primary={ms(timing.stagingDurationMs)}
        primaryLabel="duration"
        // The gap between staging and the reader's lifetime is the staging provider's own work beyond
        // consuming the source — a file-based provider uploading what it staged, for instance.
        note={beyondReader(timing)}
      />
      <Stage label="Writer" kind={timing.writerKind} primary={ms(timing.writerDurationMs)} primaryLabel="duration" />
    </div>
  )
}

function Stage({ label, kind, primary, primaryLabel, secondary, secondaryLabel, note }: {
  label: string
  kind: string | null
  primary: string
  primaryLabel: string
  secondary?: string
  secondaryLabel?: string
  note?: string | null
}) {
  return (
    <div className="timing-stage">
      <span className="timing-stage-name">{label}</span>
      <span className="mono sm">{kind ?? '—'}</span>
      <span className="timing-figure">{primary}<span className="faint"> {primaryLabel}</span></span>
      {secondary !== undefined && (
        <span className="timing-figure">{secondary}<span className="faint"> {secondaryLabel}</span></span>
      )}
      {note && <span className="faint">{note}</span>}
    </div>
  )
}

/** "Not measured" and "measured as zero" are different answers, and the column keeps them apart. */
function ms(value: number | null): string {
  if (value === null) return '—'
  if (value < 1000) return `${value}ms`
  return value < 60_000 ? `${(value / 1000).toFixed(1)}s` : `${Math.round(value / 60_000)}m`
}

function share(part: number | null, whole: number | null): string | null {
  if (part === null || whole === null || whole <= 0) return null
  return `${Math.round((part / whole) * 100)}% of the read spent waiting for the first row`
}

function beyondReader(timing: RunTiming): string | null {
  const { stagingDurationMs: staging, readerLifetimeMs: reader } = timing
  if (staging === null || reader === null) return null
  const beyond = staging - reader
  // Under a tick either way is a provider writing straight through as rows arrive, which is the
  // ordinary case and not worth a line of its own.
  return beyond > 50 ? `${ms(beyond)} of it after the source was exhausted` : null
}
