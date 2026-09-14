import { useEffect, useRef, useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Field } from '../../components/Field'
import {
  useReconcileDeletes,
  useReplication,
  useSegmentingPreview,
  useTableMapping,
  useTableMappings,
} from '../../api/hooks'
import type { BatchReloadSegment, SegmentMode } from '../../api/types'
import { runsAgainstAConnection } from '../../api/types'

/**
 * Queues an ad-hoc delete-diff sweep of one table mapping — phase 124. Same segment-picker shape as
 * {@link BulkLoadForm} (opens pre-filled from the mapping's own default segmenting), but with no
 * reader/cache/writer pickers: a sweep always runs KeyReconcile/StagingTable, ending in either
 * KeyReconcileDelete or, since phase 129, KeyReconcileScd2Close when the selected mapping's own writer
 * is Scd2 — there is nothing else this action means, but which of the two endings it means is worth
 * saying (see resolvedReconcileWriterKind below).
 */

/** Mirrors `PipelineResolution.ReconcileWriterKind`'s own resolution client-side: an explicit
 * `ReconcileConfig.Writer` override (mapping level, then replication level) wins outright; failing
 * that, the mapping's own resolved Change Processing writer decides the default. */
function resolvedReconcileWriterKind(
  mapping: { writerOverride?: { kind: string } | null; reconcileOverride?: { writer?: { kind: string } | null } | null } | undefined,
  replication: { changeProcessing: { writer: { kind: string } }; reconcile: { writer?: { kind: string } | null } } | undefined,
): string | null {
  if (!mapping || !replication) return null
  const stated = mapping.reconcileOverride?.writer?.kind ?? replication.reconcile.writer?.kind
  if (stated) return stated
  const primaryWriterKind = mapping.writerOverride?.kind ?? replication.changeProcessing.writer.kind
  return primaryWriterKind === 'Scd2' ? 'KeyReconcileScd2Close' : 'KeyReconcileDelete'
}

export function ReconcileDeletesForm({ replicationName, onQueued, onClose }: {
  replicationName: string
  onQueued: (runIds: string[]) => void
  onClose: () => void
}) {
  const { data: mappingNames } = useTableMappings(replicationName)
  const reconcile = useReconcileDeletes(replicationName)
  const { data: replication } = useReplication(replicationName)

  const [mappingName, setMappingName] = useState<string | null>(null)
  const [mode, setMode] = useState<SegmentMode>('full')
  const [column, setColumn] = useState('')
  const [values, setValues] = useState('')
  const [rangeMin, setRangeMin] = useState('')
  const [rangeMax, setRangeMax] = useState('')
  const [bucketCount, setBucketCount] = useState(4)
  const [strategyName, setStrategyName] = useState<string | null>(null)
  const [checked, setChecked] = useState<Record<number, boolean>>({})
  const [overrideGuard, setOverrideGuard] = useState(false)

  const selectedMapping = mappingName ?? mappingNames?.[0] ?? ''
  const strategies = replication?.segmentingStrategies ?? []
  const { data: mapping } = useTableMapping(replicationName, selectedMapping || undefined)

  // Pre-fill from the mapping's stored default whenever the chosen mapping changes — see
  // BulkLoadForm's identical effect for why isMappingChange guards the very first load.
  const seededFor = useRef<string | null>(null)
  useEffect(() => {
    const name = mapping?.name
    if (!name || seededFor.current === name) return
    const isMappingChange = seededFor.current !== null
    seededFor.current = name

    const stored = mapping.defaultSegmenting?.[0]
    if (!stored) {
      if (isMappingChange) setMode('full')
      return
    }
    setMode(stored.mode)
    if (stored.mode === 'list') {
      setColumn(stored.column)
      setValues(stored.values.join(', '))
    } else if (stored.mode === 'range') {
      setColumn(stored.column)
      setRangeMin(stored.rangeMin)
      setRangeMax(stored.rangeMax)
    } else if (stored.mode === 'auto') {
      setColumn(stored.column)
      setBucketCount(stored.bucketCount)
    } else if (stored.mode === 'custom') {
      setStrategyName(stored.strategyName)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mapping?.name])

  const strategy = strategies.find((s) => s.name === strategyName)
  const preview = useSegmentingPreview(
    replicationName,
    selectedMapping,
    mode === 'custom' ? strategyName : null,
  )
  const candidates = preview.data?.candidates ?? []

  useEffect(() => {
    setChecked(Object.fromEntries(candidates.map((c, i) => [i, c.selected])))
  }, [preview.data])

  const chosen = candidates.filter((_, i) => checked[i])

  const buildSegments = (): BatchReloadSegment[] => {
    switch (mode) {
      case 'list':
        return [{ mode, column, values: values.split(',').map((v) => v.trim()).filter(Boolean) }]
      case 'range':
        return [{ mode, column, rangeMin, rangeMax }]
      case 'auto':
        return [{ mode, column, bucketCount }]
      case 'custom':
        return chosen.map((c) => c.segment)
      default:
        return [{ mode: 'full' }]
    }
  }

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    const result = await reconcile.mutateAsync({
      mappingName: selectedMapping,
      request: { segments: buildSegments(), overrideGuard },
    })
    onQueued(result.runIds)
  }

  const nothingChosen = mode === 'custom' && chosen.length === 0
  const closesVersionsInstead = resolvedReconcileWriterKind(mapping, replication) === 'KeyReconcileScd2Close'

  return (
    <form className="card" style={{ width: 288, flex: 'none' }} onSubmit={submit} data-testid="reconcile-deletes-form">
      <div className="card-head tight">
        <span className="card-title sm">Reconcile deletes</span>
        <button type="button" className="btn-link quiet spacer" onClick={onClose}>Close</button>
      </div>
      <div className="card-body" style={{ gap: 10 }}>
        <ErrorBanner error={reconcile.error} />
        <span className="hint" data-testid="reconcile-mechanism-hint">
          Reads only the source's primary-key values within the scope below and {closesVersionsInstead
            ? 'closes the version of target rows whose key is no longer there'
            : 'removes target rows whose key is no longer there'}. Never inserts or updates — an updated
          source row is a job for the ordinary sync, not this.
        </span>

        <Field label="Table mapping">
          <select
            className="select" value={selectedMapping} onChange={(e) => setMappingName(e.target.value)}
            data-testid="reconcile-mapping-select"
          >
            {(mappingNames ?? []).map((m) => <option key={m} value={m}>{m}</option>)}
          </select>
        </Field>

        <Field label="Segment">
          <select className="select" value={mode} onChange={(e) => setMode(e.target.value as SegmentMode)} data-testid="reconcile-mode-select">
            <option value="full">Full — whole table</option>
            <option value="list">List — specific values</option>
            <option value="range">Range — between bounds</option>
            <option value="auto">Auto — split into buckets</option>
            <option value="custom">Custom — a segmenting strategy</option>
          </select>
        </Field>

        {mode !== 'full' && mode !== 'custom' && (
          <Field label="Source column">
            <input className="input" required value={column} onChange={(e) => setColumn(e.target.value)} data-testid="reconcile-column-input" />
          </Field>
        )}
        {mode === 'list' && (
          <Field label="Values (comma-separated)">
            <input className="input" required value={values} onChange={(e) => setValues(e.target.value)} data-testid="reconcile-values-input" />
          </Field>
        )}
        {mode === 'range' && (
          <>
            <Field label="From (inclusive)">
              <input className="input" required value={rangeMin} onChange={(e) => setRangeMin(e.target.value)} data-testid="reconcile-min-input" />
            </Field>
            <Field label="To (exclusive)">
              <input className="input" required value={rangeMax} onChange={(e) => setRangeMax(e.target.value)} data-testid="reconcile-max-input" />
            </Field>
          </>
        )}
        {mode === 'auto' && (
          <Field label="Buckets">
            <input
              className="input" type="number" min={1} value={bucketCount}
              onChange={(e) => setBucketCount(Number(e.target.value))}
              data-testid="reconcile-buckets-input"
            />
          </Field>
        )}

        {mode === 'custom' && (
          <>
            <Field label="Strategy">
              <select
                className="select"
                value={strategyName ?? ''}
                onChange={(e) => setStrategyName(e.target.value || null)}
                data-testid="reconcile-strategy-select"
              >
                <option value="">Pick a strategy…</option>
                {strategies.map((s) => <option key={s.name} value={s.name}>{s.name} — {s.kind}</option>)}
              </select>
            </Field>

            {strategies.length === 0 && (
              <span className="hint" data-testid="reconcile-no-strategies">
                This replication defines no segmenting strategies yet.
              </span>
            )}

            {strategy && runsAgainstAConnection(strategy.kind) && (
              <span className="hint" data-testid="reconcile-strategy-connection-note">
                Running this strategy queries the {strategy.kind === 'TargetSql' ? 'target' : 'source'} database.
              </span>
            )}

            <ErrorBanner error={preview.error} />
            {preview.isFetching && <span className="hint">Running the strategy…</span>}

            {candidates.length > 0 && (
              <div data-testid="reconcile-candidates">
                <div className="row" style={{ gap: 8, alignItems: 'center', marginBottom: 6 }}>
                  <button
                    type="button"
                    className="btn-link quiet"
                    onClick={() => setChecked(Object.fromEntries(candidates.map((_, i) => [i, true])))}
                    data-testid="reconcile-select-all"
                  >
                    Select all
                  </button>
                  <button
                    type="button"
                    className="btn-link quiet"
                    onClick={() => setChecked({})}
                    data-testid="reconcile-select-none"
                  >
                    None
                  </button>
                  <span className="hint spacer">{chosen.length} of {candidates.length}</span>
                </div>
                <div style={{ maxHeight: 220, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 4 }}>
                  {candidates.map((candidate, i) => (
                    <label key={`${candidate.label}-${i}`} className="row" style={{ gap: 6, alignItems: 'center' }}>
                      <input
                        type="checkbox"
                        checked={checked[i] ?? false}
                        onChange={(e) => setChecked((prev) => ({ ...prev, [i]: e.target.checked }))}
                        data-testid={`reconcile-candidate-${i}`}
                      />
                      <span className="mono sm">{candidate.label}</span>
                    </label>
                  ))}
                </div>
              </div>
            )}

            {strategyName && !preview.isFetching && candidates.length === 0 && !preview.error && (
              <span className="hint" data-testid="reconcile-no-candidates">
                This strategy proposed no segments.
              </span>
            )}
          </>
        )}

        <label className="row" style={{ gap: 6, alignItems: 'center' }}>
          <input
            type="checkbox"
            checked={overrideGuard}
            onChange={(e) => setOverrideGuard(e.target.checked)}
            data-testid="reconcile-override-guard-checkbox"
          />
          <span className="hint">
            Allow a large deletion — skips the guard that otherwise refuses a sweep deleting more than
            half of the scope's rows.
          </span>
        </label>

        <button
          type="submit"
          className="btn btn-primary"
          style={{ alignSelf: 'flex-start' }}
          disabled={reconcile.isPending || !selectedMapping || nothingChosen}
          data-testid="reconcile-submit-button"
        >
          {reconcile.isPending
            ? 'Queueing…'
            : mode === 'custom' && chosen.length > 0
              ? `Queue ${chosen.length} segment${chosen.length === 1 ? '' : 's'}`
              : 'Queue sweep'}
        </button>
      </div>
    </form>
  )
}
