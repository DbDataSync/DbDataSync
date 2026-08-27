import { useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Field } from '../../components/Field'
import { useBackfill, useReplicationCapabilities, useTableMappings } from '../../api/hooks'
import type { BatchReloadSegment, SegmentMode } from '../../api/types'

/**
 * Queues an ad-hoc reload of one table mapping. Sits beside the live-run panel in the design, as a
 * 288px column rather than a band across the top.
 */
export function BackfillForm({ replicationName, onQueued, onClose }: {
  replicationName: string
  onQueued: (runIds: string[]) => void
  onClose: () => void
}) {
  const { data: mappingNames } = useTableMappings(replicationName)
  const capabilities = useReplicationCapabilities(replicationName)
  const backfill = useBackfill(replicationName)

  const [mappingName, setMappingName] = useState<string | null>(null)
  const [mode, setMode] = useState<SegmentMode>('full')
  const [column, setColumn] = useState('')
  const [values, setValues] = useState('')
  const [rangeMin, setRangeMin] = useState('')
  const [rangeMax, setRangeMax] = useState('')
  const [bucketCount, setBucketCount] = useState(4)
  const [readerKind, setReaderKind] = useState<string | null>(null)
  const [cacheKind, setCacheKind] = useState<string | null>(null)
  const [writerKind, setWriterKind] = useState<string | null>(null)

  // Defaults are picked by capability, not by name: a reload needs a reader that can be scoped to a
  // segment and a writer that removes rows the source no longer has.
  const selectedMapping = mappingName ?? mappingNames?.[0] ?? ''
  const selectedReader =
    readerKind ?? (capabilities.readers.find((r) => r.supportsSegmentation) ?? capabilities.readers[0])?.kind ?? ''
  const selectedCache = cacheKind ?? capabilities.stagingProviders[0]?.kind ?? ''
  const selectedWriter =
    writerKind ?? (capabilities.writers.find((w) => w.supportsReconciliation) ?? capabilities.writers[0])?.kind ?? ''

  const writer = capabilities.writers.find((w) => w.kind === selectedWriter)

  const buildSegment = (): BatchReloadSegment => {
    switch (mode) {
      case 'list':
        return { mode, column, values: values.split(',').map((v) => v.trim()).filter(Boolean) }
      case 'range':
        return { mode, column, rangeMin, rangeMax }
      case 'auto':
        return { mode, column, bucketCount }
      default:
        return { mode: 'full' }
    }
  }

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    const result = await backfill.mutateAsync({
      mappingName: selectedMapping,
      request: { readerKind: selectedReader, cacheKind: selectedCache, writerKind: selectedWriter, segments: [buildSegment()] },
    })
    onQueued(result.runIds)
  }

  return (
    <form className="card" style={{ width: 288, flex: 'none' }} onSubmit={submit} data-testid="backfill-form">
      <div className="card-head tight">
        <span className="card-title sm">Backfill</span>
        <button type="button" className="btn-link quiet spacer" onClick={onClose}>Close</button>
      </div>
      <div className="card-body" style={{ gap: 10 }}>
        <ErrorBanner error={backfill.error ?? capabilities.error} />

        <Field label="Table mapping">
          <select className="select" value={selectedMapping} onChange={(e) => setMappingName(e.target.value)} data-testid="backfill-mapping-select">
            {(mappingNames ?? []).map((m) => <option key={m} value={m}>{m}</option>)}
          </select>
        </Field>

        <Field label="Segment">
          <select className="select" value={mode} onChange={(e) => setMode(e.target.value as SegmentMode)} data-testid="backfill-mode-select">
            <option value="full">Full — whole table</option>
            <option value="list">List — specific values</option>
            <option value="range">Range — between bounds</option>
            <option value="auto">Auto — split into buckets</option>
          </select>
        </Field>

        {mode !== 'full' && (
          <Field label="Source column">
            <input className="input" required value={column} onChange={(e) => setColumn(e.target.value)} data-testid="backfill-column-input" />
          </Field>
        )}
        {mode === 'list' && (
          <Field label="Values (comma-separated)">
            <input className="input" required value={values} onChange={(e) => setValues(e.target.value)} data-testid="backfill-values-input" />
          </Field>
        )}
        {mode === 'range' && (
          <>
            <Field label="From (inclusive)">
              <input className="input" required value={rangeMin} onChange={(e) => setRangeMin(e.target.value)} data-testid="backfill-min-input" />
            </Field>
            <Field label="To (exclusive)">
              <input className="input" required value={rangeMax} onChange={(e) => setRangeMax(e.target.value)} data-testid="backfill-max-input" />
            </Field>
          </>
        )}
        {mode === 'auto' && (
          <Field label="Buckets">
            <input
              className="input" type="number" min={1} value={bucketCount}
              onChange={(e) => setBucketCount(Number(e.target.value))}
              data-testid="backfill-buckets-input"
            />
          </Field>
        )}

        <Field label="Reader">
          <select className="select" value={selectedReader} onChange={(e) => setReaderKind(e.target.value)} data-testid="backfill-reader-select">
            {capabilities.readers.map((r) => (
              <option key={r.kind} value={r.kind}>{r.supportsSegmentation ? `${r.kind} — segmentable` : r.kind}</option>
            ))}
          </select>
        </Field>
        <Field label="Staging">
          <select className="select" value={selectedCache} onChange={(e) => setCacheKind(e.target.value)} data-testid="backfill-cache-select">
            {capabilities.stagingProviders.map((p) => <option key={p.kind} value={p.kind}>{p.kind}</option>)}
          </select>
        </Field>
        <Field label="Writer">
          <select className="select" value={selectedWriter} onChange={(e) => setWriterKind(e.target.value)} data-testid="backfill-writer-select">
            {capabilities.writers.map((w) => (
              <option key={w.kind} value={w.kind}>{w.supportsReconciliation ? `${w.kind} — reconciling` : `${w.kind} — upsert-only`}</option>
            ))}
          </select>
        </Field>

        {writer && !writer.supportsReconciliation && (
          <span className="hint" data-testid="backfill-upsert-note">
            <span className="mono">{writer.kind}</span> only adds and updates rows — rows deleted at the source
            will stay in the target.
          </span>
        )}

        <button
          type="submit"
          className="btn btn-primary"
          style={{ alignSelf: 'flex-start' }}
          disabled={backfill.isPending || !selectedMapping}
          data-testid="backfill-submit-button"
        >
          {backfill.isPending ? 'Queueing…' : 'Queue backfill'}
        </button>
      </div>
    </form>
  )
}
