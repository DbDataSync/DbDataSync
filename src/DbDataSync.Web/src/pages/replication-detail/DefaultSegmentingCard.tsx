import { Field } from '../../components/Field'
import type { BatchReloadSegment, SegmentingStrategyConfig } from '../../api/types'
import { runsAgainstAConnection } from '../../api/types'

/**
 * How this mapping divides for a reload, by default — what a scheduled `BatchReload` pass processes,
 * and what the Bulk Load form opens pre-filled to.
 *
 * A list, because that is genuinely what it is: any number of List entries and any number of Range
 * entries side by side. Full, Auto and Custom are the other shape — one entry that expands into many
 * at reload time. This is what replaced the hand-typed JSON `segments` reader option; the shape on
 * disk is the same array it always was, but nobody types it any more.
 */
export function DefaultSegmentingCard({ segments, strategies, onChange }: {
  segments: BatchReloadSegment[]
  strategies: SegmentingStrategyConfig[]
  onChange: (segments: BatchReloadSegment[]) => void
}) {
  const replace = (index: number, segment: BatchReloadSegment) =>
    onChange(segments.map((s, i) => (i === index ? segment : s)))

  const remove = (index: number) => onChange(segments.filter((_, i) => i !== index))

  // Full/Auto/Custom describe the whole table in one entry, so choosing one replaces the list rather
  // than joining it. Mixing "the whole table" with "these three months" is not a list of two things
  // to reload, it is two answers to the same question.
  const setSingle = (segment: BatchReloadSegment) => onChange([segment])

  const single = segments.length === 1 ? segments[0] : undefined
  const mode: 'none' | 'full' | 'auto' | 'custom' | 'static' =
    segments.length === 0 ? 'none'
      : single?.mode === 'full' ? 'full'
        : single?.mode === 'auto' ? 'auto'
          : single?.mode === 'custom' ? 'custom'
            : 'static'

  const chooseMode = (next: string) => {
    if (next === 'none') onChange([])
    else if (next === 'full') setSingle({ mode: 'full' })
    else if (next === 'auto') setSingle({ mode: 'auto', column: '', bucketCount: 4 })
    else if (next === 'custom') setSingle({ mode: 'custom', strategyName: strategies[0]?.name ?? '', column: '' })
    else if (next === 'static') onChange([{ mode: 'range', column: '', rangeMin: '', rangeMax: '' }])
  }

  const strategy = single?.mode === 'custom'
    ? strategies.find((s) => s.name === single.strategyName)
    : undefined

  return (
    <div className="card" data-testid="default-segmenting-card">
      <div className="card-head">
        <span className="card-title">Default reload segmenting</span>
      </div>
      <div className="card-body" style={{ gap: 10 }}>
        <Field label="How this table divides for a reload">
          <select
            className="select"
            value={mode}
            onChange={(e) => chooseMode(e.target.value)}
            data-testid="default-segmenting-mode"
          >
            <option value="none">Full — the whole table, unsegmented</option>
            <option value="full">Full — stated explicitly</option>
            <option value="auto">Auto — split a column into buckets</option>
            <option value="custom">Custom — a segmenting strategy</option>
            <option value="static">A fixed list of segments</option>
          </select>
        </Field>

        {mode === 'none' && (
          <span className="hint" data-testid="default-segmenting-none-hint">
            Nothing configured, so a reload covers the whole table — which is what it has always done.
          </span>
        )}

        {mode === 'auto' && single?.mode === 'auto' && (
          <div className="row" style={{ gap: 8 }}>
            <Field label="Column">
              <input
                className="input"
                value={single.column}
                onChange={(e) => setSingle({ ...single, column: e.target.value })}
                data-testid="default-segmenting-auto-column"
              />
            </Field>
            <Field label="Buckets">
              <input
                className="input" type="number" min={1}
                value={single.bucketCount}
                onChange={(e) => setSingle({ ...single, bucketCount: Number(e.target.value) })}
                data-testid="default-segmenting-auto-buckets"
              />
            </Field>
          </div>
        )}

        {mode === 'custom' && single?.mode === 'custom' && (
          <>
            <Field label="Strategy">
              <select
                className="select"
                value={single.strategyName}
                onChange={(e) => setSingle({ ...single, mode: 'custom', strategyName: e.target.value })}
                data-testid="default-segmenting-strategy"
              >
                <option value="">Pick a strategy…</option>
                {strategies.map((s) => <option key={s.name} value={s.name}>{s.name} — {s.kind}</option>)}
              </select>
            </Field>

            {strategy?.kind !== 'Script' && (
              <Field label="Column">
                <input
                  className="input"
                  value={single.column ?? ''}
                  onChange={(e) => setSingle({ ...single, column: e.target.value })}
                  placeholder="OrderDate"
                  data-testid="default-segmenting-custom-column"
                />
              </Field>
            )}

            {strategies.length === 0 && (
              <span className="hint" data-testid="default-segmenting-no-strategies">
                This replication defines no segmenting strategies yet.
              </span>
            )}

            <span className="hint">
              Stored as a reference, not as the segments it produces — so a strategy that tracks
              &ldquo;the last three months&rdquo; keeps meaning that.
            </span>

            {/*
              The one thing that has to be said out loud. A default runs unattended, on the
              replication's own schedule, forever — whether that cost is acceptable is the operator's
              judgement about their own tables, and they can only make it if they can see it.
            */}
            {strategy && runsAgainstAConnection(strategy.kind) && (
              <span className="hint warn" data-testid="default-segmenting-repeat-warning">
                <strong>This query runs on every scheduled pass.</strong>{' '}
                <span className="mono">{strategy.name}</span> queries the{' '}
                {strategy.kind === 'TargetSql' ? 'target' : 'source'} database, and as this mapping&rsquo;s
                default it will do so every time the replication runs — not once as a preview.
              </span>
            )}
          </>
        )}

        {mode === 'static' && (
          <>
            {segments.map((segment, index) => (
              <div key={index} className="row" style={{ gap: 8, alignItems: 'flex-end' }}>
                <Field label="Kind">
                  <select
                    className="select"
                    value={segment.mode}
                    onChange={(e) => replace(index, e.target.value === 'list'
                      ? { mode: 'list', column: '', values: [] }
                      : { mode: 'range', column: '', rangeMin: '', rangeMax: '' })}
                    data-testid={`default-segmenting-kind-${index}`}
                  >
                    <option value="range">Range</option>
                    <option value="list">List</option>
                  </select>
                </Field>

                {(segment.mode === 'range' || segment.mode === 'list') && (
                  <Field label="Column">
                    <input
                      className="input"
                      value={segment.column}
                      onChange={(e) => replace(index, { ...segment, column: e.target.value })}
                      data-testid={`default-segmenting-column-${index}`}
                    />
                  </Field>
                )}

                {segment.mode === 'range' && (
                  <>
                    <Field label="From (inclusive)">
                      <input
                        className="input"
                        value={segment.rangeMin}
                        onChange={(e) => replace(index, { ...segment, rangeMin: e.target.value })}
                        data-testid={`default-segmenting-min-${index}`}
                      />
                    </Field>
                    <Field label="To (exclusive)">
                      <input
                        className="input"
                        value={segment.rangeMax}
                        onChange={(e) => replace(index, { ...segment, rangeMax: e.target.value })}
                        data-testid={`default-segmenting-max-${index}`}
                      />
                    </Field>
                  </>
                )}

                {segment.mode === 'list' && (
                  <Field label="Values (comma-separated)">
                    <input
                      className="input"
                      value={segment.values.join(', ')}
                      onChange={(e) => replace(index, {
                        ...segment,
                        values: e.target.value.split(',').map((v) => v.trim()).filter(Boolean),
                      })}
                      data-testid={`default-segmenting-values-${index}`}
                    />
                  </Field>
                )}

                <button
                  type="button"
                  className="btn-link quiet"
                  onClick={() => remove(index)}
                  data-testid={`default-segmenting-remove-${index}`}
                >
                  Remove
                </button>
              </div>
            ))}

            <div className="row" style={{ gap: 8 }}>
              <button
                type="button"
                className="btn"
                onClick={() => onChange([...segments, { mode: 'range', column: '', rangeMin: '', rangeMax: '' }])}
                data-testid="default-segmenting-add-range"
              >
                Add range
              </button>
              <button
                type="button"
                className="btn"
                onClick={() => onChange([...segments, { mode: 'list', column: '', values: [] }])}
                data-testid="default-segmenting-add-list"
              >
                Add list
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  )
}
