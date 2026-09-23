import { useEffect, useRef, useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Field } from '../../components/Field'
import {
  useBulkLoad,
  useCapabilities,
  useReplication,
  useSegmentingPreview,
  useTableMapping,
  useTableMappings,
} from '../../api/hooks'
import { resolveSide } from '../../api/resolveEndpoint'
import type { BatchReloadSegment, SegmentMode } from '../../api/types'
import { RECONCILE_ONLY_KINDS, runsAgainstAConnection } from '../../api/types'
import { readerNotes } from '../../api/readerNotes'

/**
 * Queues an ad-hoc reload of one table mapping. Sits beside the live-run panel in the design, as a
 * 288px column rather than a band across the top.
 *
 * Opens pre-filled from the mapping's own default segmenting, so a table that is always reloaded the
 * same way does not have to be re-described every time. Everything stays editable for this one
 * bulk load: ad hoc means ad hoc, and nothing typed here writes back to the mapping.
 */
export function BulkLoadForm({ replicationName, onQueued, onClose }: {
  replicationName: string
  onQueued: (runIds: string[]) => void
  onClose: () => void
}) {
  const { data: mappingNames } = useTableMappings(replicationName)
  const bulkLoad = useBulkLoad(replicationName)
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
  const [readerKind, setReaderKind] = useState<string | null>(null)
  const [cacheKind, setCacheKind] = useState<string | null>(null)
  const [writerKind, setWriterKind] = useState<string | null>(null)

  const selectedMapping = mappingName ?? mappingNames?.[0] ?? ''
  const { data: mapping } = useTableMapping(replicationName, selectedMapping || undefined)

  // Each side's own driver, not the replication's *first* mapping's — a reader Kind is the selected
  // mapping's own source's question, staging and the writer are its target's, the same split
  // MappingPipelineCard already uses for a mapping's regular pipeline. Found as a real bug (not
  // assumed): this used to be useReplicationCapabilities(replicationName), which is keyed to
  // whichever mapping happens to be first in the replication — so a bulk load against a JDBC-sourced
  // mapping could see an unrelated MsSql-sourced mapping's reader capabilities instead, if that one
  // happened to sort first, and default itself into a reader the actual selected mapping's driver
  // never offered ("MsSqlBatchReload" surfacing for a JDBC connector with no MsSql driver in sight).
  const resolvedSource = mapping ? resolveSide(replication?.endpoints.source ?? null, mapping.sources[0]) : null
  const resolvedTarget = mapping ? resolveSide(replication?.endpoints.target ?? null, mapping.targets[0]) : null
  const sourceCapabilities = useCapabilities(resolvedSource?.connectionName || undefined)
  const targetCapabilities = useCapabilities(resolvedTarget?.connectionName || undefined)
  const capabilities = {
    readers: sourceCapabilities.data?.readers ?? [],
    stagingProviders: targetCapabilities.data?.stagingProviders ?? [],
    writers: targetCapabilities.data?.writers ?? [],
    error: sourceCapabilities.error ?? targetCapabilities.error,
  }

  // KeyReconcile/KeyReconcileDelete (phase 124) exist only for a delete-diff sweep — that action has
  // its own trigger, not a Kind an operator picks here.
  const availableReaders = capabilities.readers.filter((r) => !RECONCILE_ONLY_KINDS.has(r.kind))
  const availableWriters = capabilities.writers.filter((w) => !RECONCILE_ONLY_KINDS.has(w.kind))

  // The mapping's own override, or the replication's own bulk-load default — whichever an operator
  // actually configured — wins over a freshly-guessed capability default, and only when it is: (a)
  // unset, or (b) no longer valid for the now-correctly-scoped driver above (a real, if rare,
  // possibility if a mapping's source connection changes to a different engine after the override was
  // saved). Found as a real bug (not assumed): before this, the reader Kind an operator explicitly
  // saved for a mapping's bulk loads was never read here at all — this dialog re-guessed a Kind from
  // capabilities on every open, silently overriding a deliberate choice with "whichever reader
  // supports segmentation" every single time.
  const savedReaderKind = mapping?.bulkLoadReaderOverride?.kind ?? replication?.bulkLoad.reader.kind
  const effectiveReaderKind = savedReaderKind && availableReaders.some((r) => r.kind === savedReaderKind)
    ? savedReaderKind
    : undefined

  // Defaults are picked by capability, not by name, only once there is no saved choice to honour: a
  // reload needs a reader that can be scoped to a segment and a writer that removes rows the source no
  // longer has.
  const selectedReader =
    readerKind ?? effectiveReaderKind ?? (availableReaders.find((r) => r.supportsSegmentation) ?? availableReaders[0])?.kind ?? ''
  const selectedCache = cacheKind ?? capabilities.stagingProviders[0]?.kind ?? ''
  const selectedWriter =
    writerKind ?? (availableWriters.find((w) => w.supportsReconciliation) ?? availableWriters[0])?.kind ?? ''

  const writer = availableWriters.find((w) => w.kind === selectedWriter)
  const strategies = replication?.segmentingStrategies ?? []

  // Pre-fill from the mapping's stored default whenever the chosen mapping changes. Only the first
  // entry drives the form's mode controls — the form edits one segment, while a stored default may
  // be a list; a multi-entry default is honoured on the *scheduled* path, and here it seeds the
  // shape rather than pretending the form can show all of it.
  const seededFor = useRef<string | null>(null)
  useEffect(() => {
    const name = mapping?.name
    // Wait for the mapping query to resolve — running while it is undefined would seed off nothing.
    if (!name || seededFor.current === name) return
    // The `undefined -> loaded` transition of the *initial* mapping is not a mapping change: the form
    // already starts on 'full', and treating it as one resets a mode the operator picked in the
    // moment before this query landed (which a fast open, or a slow API, makes a real window).
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
      setColumn(stored.column ?? '')
    }
    // Keyed on which mapping this is, and nothing else. `defaultSegmenting` is a fresh array on every
    // refetch, so depending on it meant any background refetch — an invalidation from somewhere else
    // on the page — re-ran this and reset a mode the operator had just chosen. "Whenever the chosen
    // mapping changes" is what the pre-fill is for; the identity of a re-fetched array is not that.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mapping?.name])

  const strategy = strategies.find((s) => s.name === strategyName)
  const preview = useSegmentingPreview(
    replicationName,
    selectedMapping,
    mode === 'custom' ? strategyName : null,
    mode === 'custom' ? column || null : null,
  )
  const candidates = preview.data?.candidates ?? []

  // The strategy's own flags decide which boxes start ticked; the operator decides the rest. Keyed
  // by index and reset whenever a fresh proposal arrives, since a re-run against "today" may not
  // propose the same segments it did a moment ago.
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
        // The checked candidates themselves, already concrete ranges — not the marker. The operator
        // has just been shown exactly what will run and has said which of it they meant.
        return chosen.map((c) => c.segment)
      default:
        return [{ mode: 'full' }]
    }
  }

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    const result = await bulkLoad.mutateAsync({
      mappingName: selectedMapping,
      request: {
        readerKind: selectedReader,
        cacheKind: selectedCache,
        writerKind: selectedWriter,
        segments: buildSegments(),
      },
    })
    onQueued(result.runIds)
  }

  const nothingChosen = mode === 'custom' && chosen.length === 0

  return (
    <form className="card" style={{ width: 288, flex: 'none' }} onSubmit={submit} data-testid="bulk-load-form">
      <div className="card-head tight">
        <span className="card-title sm">Bulk Load</span>
        <button type="button" className="btn-link quiet spacer" onClick={onClose}>Close</button>
      </div>
      <div className="card-body" style={{ gap: 10 }}>
        <ErrorBanner error={bulkLoad.error ?? capabilities.error} />

        <Field label="Table mapping">
          <select className="select" value={selectedMapping} onChange={(e) => setMappingName(e.target.value)} data-testid="bulk-load-mapping-select">
            {(mappingNames ?? []).map((m) => <option key={m} value={m}>{m}</option>)}
          </select>
        </Field>

        <Field label="Segment">
          <select className="select" value={mode} onChange={(e) => setMode(e.target.value as SegmentMode)} data-testid="bulk-load-mode-select">
            <option value="full">Full — whole table</option>
            <option value="list">List — specific values</option>
            <option value="range">Range — between bounds</option>
            <option value="auto">Auto — split into buckets</option>
            <option value="custom">Custom — a segmenting strategy</option>
          </select>
        </Field>

        {mode !== 'full' && (
          <Field label="Source column">
            <input
              className="input"
              required={mode !== 'custom'}
              value={column}
              onChange={(e) => setColumn(e.target.value)}
              data-testid="bulk-load-column-input"
            />
          </Field>
        )}
        {mode === 'list' && (
          <Field label="Values (comma-separated)">
            <input className="input" required value={values} onChange={(e) => setValues(e.target.value)} data-testid="bulk-load-values-input" />
          </Field>
        )}
        {mode === 'range' && (
          <>
            <Field label="From (inclusive)">
              <input className="input" required value={rangeMin} onChange={(e) => setRangeMin(e.target.value)} data-testid="bulk-load-min-input" />
            </Field>
            <Field label="To (exclusive)">
              <input className="input" required value={rangeMax} onChange={(e) => setRangeMax(e.target.value)} data-testid="bulk-load-max-input" />
            </Field>
          </>
        )}
        {mode === 'auto' && (
          <Field label="Buckets">
            <input
              className="input" type="number" min={1} value={bucketCount}
              onChange={(e) => setBucketCount(Number(e.target.value))}
              data-testid="bulk-load-buckets-input"
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
                data-testid="bulk-load-strategy-select"
              >
                <option value="">Pick a strategy…</option>
                {strategies.map((s) => <option key={s.name} value={s.name}>{s.name} — {s.kind}</option>)}
              </select>
            </Field>

            {strategies.length === 0 && (
              <span className="hint" data-testid="bulk-load-no-strategies">
                This replication defines no segmenting strategies yet.
              </span>
            )}

            {strategy && runsAgainstAConnection(strategy.kind) && (
              <span className="hint" data-testid="bulk-load-strategy-connection-note">
                Running this strategy queries the {strategy.kind === 'TargetSql' ? 'target' : 'source'} database.
              </span>
            )}

            <ErrorBanner error={preview.error} />
            {preview.isFetching && <span className="hint">Running the strategy…</span>}

            {candidates.length > 0 && (
              <div data-testid="bulk-load-candidates">
                <div className="row" style={{ gap: 8, alignItems: 'center', marginBottom: 6 }}>
                  <button
                    type="button"
                    className="btn-link quiet"
                    onClick={() => setChecked(Object.fromEntries(candidates.map((_, i) => [i, true])))}
                    data-testid="bulk-load-select-all"
                  >
                    Select all
                  </button>
                  <button
                    type="button"
                    className="btn-link quiet"
                    onClick={() => setChecked({})}
                    data-testid="bulk-load-select-none"
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
                        data-testid={`bulk-load-candidate-${i}`}
                      />
                      <span className="mono sm">{candidate.label}</span>
                    </label>
                  ))}
                </div>
              </div>
            )}

            {strategyName && !preview.isFetching && candidates.length === 0 && !preview.error && (
              <span className="hint" data-testid="bulk-load-no-candidates">
                This strategy proposed no segments.
              </span>
            )}
          </>
        )}

        <Field label="Reader">
          <select className="select" value={selectedReader} onChange={(e) => setReaderKind(e.target.value)} data-testid="bulk-load-reader-select">
            {availableReaders.map((r) => (
              <option key={r.kind} value={r.kind}>{[r.kind, ...readerNotes(r)].join(' — ')}</option>
            ))}
          </select>
        </Field>
        <Field label="Staging">
          <select className="select" value={selectedCache} onChange={(e) => setCacheKind(e.target.value)} data-testid="bulk-load-cache-select">
            {capabilities.stagingProviders.map((p) => <option key={p.kind} value={p.kind}>{p.kind}</option>)}
          </select>
        </Field>
        <Field label="Writer">
          <select className="select" value={selectedWriter} onChange={(e) => setWriterKind(e.target.value)} data-testid="bulk-load-writer-select">
            {availableWriters.map((w) => (
              <option key={w.kind} value={w.kind}>{w.supportsReconciliation ? `${w.kind} — reconciling` : `${w.kind} — upsert-only`}</option>
            ))}
          </select>
        </Field>

        {writer && !writer.supportsReconciliation && (
          <span className="hint" data-testid="bulk-load-upsert-note">
            <span className="mono">{writer.kind}</span> only adds and updates rows — rows deleted at the source
            will stay in the target.
          </span>
        )}

        <button
          type="submit"
          className="btn btn-primary"
          style={{ alignSelf: 'flex-start' }}
          disabled={bulkLoad.isPending || !selectedMapping || nothingChosen}
          data-testid="bulk-load-submit-button"
        >
          {bulkLoad.isPending
            ? 'Queueing…'
            : mode === 'custom' && chosen.length > 0
              ? `Queue ${chosen.length} segment${chosen.length === 1 ? '' : 's'}`
              : 'Queue bulk load'}
        </button>
      </div>
    </form>
  )
}
