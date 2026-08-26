import { useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { KindSelect } from '../../components/KindSelect'
import { readerOptions, stagingOptions, writerOptions } from '../../components/kindOptions'
import { useBackfill, useReplicationCapabilities, useTableMappings } from '../../api/hooks'
import type { BatchReloadSegment, SegmentMode } from '../../api/types'

/**
 * Queues an ad-hoc reload of one table mapping. Separate from "Run Now" because it is a different
 * thing: it targets one mapping rather than the replication, it re-reads data an incremental pass has
 * already seen, and it never advances the incremental watermark — so it can be run against a live,
 * scheduled replication without disturbing it.
 *
 * One segment per submission in this first pass. The API accepts an array, so multi-segment
 * submission is additive here later, not a contract change.
 */
export function BackfillForm({
  replicationName,
  onQueued,
  onClose,
}: {
  replicationName: string
  onQueued: (runIds: string[]) => void
  onClose: () => void
}) {
  const { data: mappingNames } = useTableMappings(replicationName)
  const capabilities = useReplicationCapabilities(replicationName)
  const backfill = useBackfill(replicationName)

  // Selections are null until the operator makes one, with the effective value derived during render
  // from what the driver offers. Seeding state from an effect instead would render one frame of empty
  // pickers and re-render on arrival, and would quietly keep a stale choice if the options changed.
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

  // Defaults are picked by *capability*, not by name: a reload needs a reader that can be scoped to a
  // segment and a writer that removes rows the source no longer has, and which Kinds those happen to
  // be is the driver's business. Falls back to whatever exists if the driver offers neither.
  const selectedMapping = mappingName ?? mappingNames?.[0] ?? ''
  const selectedReader =
    readerKind ?? (capabilities.readers.find((r) => r.supportsSegmentation) ?? capabilities.readers[0])?.kind ?? ''
  const selectedCache = cacheKind ?? capabilities.stagingProviders[0]?.kind ?? ''
  const selectedWriterKind =
    writerKind ?? (capabilities.writers.find((w) => w.supportsReconciliation) ?? capabilities.writers[0])?.kind ?? ''

  const buildSegment = (): BatchReloadSegment => {
    switch (mode) {
      case 'list':
        return {
          mode,
          column,
          values: values
            .split(',')
            .map((v) => v.trim())
            .filter((v) => v !== ''),
        }
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
      request: {
        readerKind: selectedReader,
        cacheKind: selectedCache,
        writerKind: selectedWriterKind,
        segments: [buildSegment()],
      },
    })
    onQueued(result.runIds)
  }

  const selectedWriter = capabilities.writers.find((w) => w.kind === selectedWriterKind)

  return (
    <form className="subform stack" onSubmit={submit} data-testid="backfill-form">
      <div className="row-between">
        <strong>Backfill</strong>
        <button type="button" className="btn btn-sm" onClick={onClose}>
          Close
        </button>
      </div>

      <ErrorBanner error={backfill.error ?? capabilities.error} />

      <div className="form-grid">
        <div className="form-field">
          <label>Table Mapping</label>
          <select value={selectedMapping} onChange={(e) => setMappingName(e.target.value)} data-testid="backfill-mapping-select">
            {(mappingNames ?? []).map((m) => (
              <option key={m} value={m}>
                {m}
              </option>
            ))}
          </select>
        </div>

        <div className="form-field">
          <label>Segment</label>
          <select value={mode} onChange={(e) => setMode(e.target.value as SegmentMode)} data-testid="backfill-mode-select">
            <option value="full">Full — the whole table</option>
            <option value="list">List — specific values</option>
            <option value="range">Range — between two bounds</option>
            <option value="auto">Auto — split into buckets</option>
          </select>
        </div>

        {mode !== 'full' && (
          <div className="form-field">
            <label>Source Column</label>
            <input
              required
              value={column}
              onChange={(e) => setColumn(e.target.value)}
              data-testid="backfill-column-input"
            />
          </div>
        )}

        {mode === 'list' && (
          <div className="form-field">
            <label>Values (comma-separated)</label>
            <input required value={values} onChange={(e) => setValues(e.target.value)} data-testid="backfill-values-input" />
          </div>
        )}

        {mode === 'range' && (
          <>
            <div className="form-field">
              <label>From (inclusive)</label>
              <input required value={rangeMin} onChange={(e) => setRangeMin(e.target.value)} data-testid="backfill-min-input" />
            </div>
            <div className="form-field">
              <label>To (exclusive)</label>
              <input required value={rangeMax} onChange={(e) => setRangeMax(e.target.value)} data-testid="backfill-max-input" />
            </div>
          </>
        )}

        {mode === 'auto' && (
          <div className="form-field">
            <label>Buckets</label>
            <input
              type="number"
              min={1}
              value={bucketCount}
              onChange={(e) => setBucketCount(Number(e.target.value))}
              data-testid="backfill-buckets-input"
            />
          </div>
        )}

        <KindSelect
          label="Reader"
          value={selectedReader}
          options={readerOptions(capabilities.readers)}
          onChange={setReaderKind}
          testId="backfill-reader-select"
        />
        <KindSelect
          label="Staging"
          value={selectedCache}
          options={stagingOptions(capabilities.stagingProviders)}
          onChange={setCacheKind}
          testId="backfill-cache-select"
        />
        <KindSelect
          label="Writer"
          value={selectedWriterKind}
          options={writerOptions(capabilities.writers)}
          onChange={setWriterKind}
          testId="backfill-writer-select"
        />
      </div>

      {selectedWriter && !selectedWriter.supportsReconciliation && (
        <p className="muted" data-testid="backfill-upsert-note">
          <code>{selectedWriter.kind}</code> only adds and updates rows. Rows deleted at the source since
          the last sync will stay in the target — pick a reconciling writer if the reload should remove them.
        </p>
      )}
      {mode === 'auto' && (
        <p className="muted">
          The column's range is measured now and split into {bucketCount} segment(s), each queued as its own run.
        </p>
      )}

      <div className="form-actions">
        <button
          type="submit"
          className="btn btn-primary"
          disabled={backfill.isPending || !selectedMapping}
          data-testid="backfill-submit-button"
        >
          {backfill.isPending ? 'Queueing…' : 'Queue Backfill'}
        </button>
      </div>
    </form>
  )
}
