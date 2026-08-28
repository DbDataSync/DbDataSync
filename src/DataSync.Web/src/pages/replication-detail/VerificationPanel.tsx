import { useState } from 'react'
import { Link, useOutletContext, useParams } from 'react-router-dom'
import { ErrorBanner } from '../../components/ErrorBanner'
import { useRunVerification, useVerificationResult, useVerificationResults } from '../../api/hooks'
import type { VerificationResult, VerificationResultRow, VerificationRowStatus } from '../../api/types'
import type { MappingsOutletContext } from './TableMappingsPanel'

const STATUS_LABEL: Record<VerificationRowStatus, string> = {
  Match: 'match',
  Differs: 'differs',
  MissingFromTarget: 'not at target',
  MissingFromSource: 'not at source',
}

const STATUS_DOT: Record<VerificationRowStatus, string> = {
  Match: 'dot-ok',
  Differs: 'dot-bad',
  MissingFromTarget: 'dot-warn',
  MissingFromSource: 'dot-warn',
}

/**
 * What a mapping's checks found: groupings first, then each measure's two sides and the difference.
 *
 * **Both read times are shown, and that is not decoration.** A replication is behind by design, so a
 * difference is only as meaningful as the gap between the two reads is small — without it an operator
 * is guessing whether they are looking at drift or a defect, which is the thing this screen exists to
 * stop them doing.
 */
export function VerificationPanel() {
  const { replicationName, base } = useOutletContext<MappingsOutletContext>()
  const { mappingName } = useParams<{ mappingName: string }>()

  const { data: results, error } = useVerificationResults(replicationName, mappingName)
  const run = useRunVerification(replicationName)
  const [picked, setPicked] = useState<number | undefined>()

  // The newest result until somebody picks another. Derived during render rather than set in an
  // effect: the answer is a function of what came back, and an effect would render once with nothing
  // selected before correcting itself.
  const selected = picked ?? results?.[0]?.id
  const { data: result } = useVerificationResult(replicationName, selected)

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }} data-testid="verification-panel">
      <div className="page-head">
        <h2 className="page-title mono">{mappingName}</h2>
        <span className="page-note">source and target compared, on demand</span>
        <div className="right">
          <Link className="btn" to={`${base}/${encodeURIComponent(mappingName!)}`}>Back to the mapping</Link>
          <button
            type="button"
            className="btn btn-primary"
            disabled={run.isPending}
            onClick={() => run.mutate(mappingName!)}
            data-testid="run-verification-button"
          >
            {run.isPending ? 'Queueing…' : 'Run checks'}
          </button>
        </div>
      </div>

      <ErrorBanner error={error ?? run.error} />

      <div className="card flush" data-testid="verification-results-list">
        <div className="grid-head" style={{ gridTemplateColumns: '1.2fr 1fr 1fr 1.4fr', gap: 14 }}>
          <span>Check</span><span>Groups</span><span>Differing</span><span>Run</span>
        </div>
        {results?.length === 0 && (
          <div className="empty">No results yet — run the checks to produce one.</div>
        )}
        {(results ?? []).map((r) => (
          <button
            key={r.id}
            className={`grid-row ${r.id === selected ? 'active' : ''}`}
            style={{ gridTemplateColumns: '1.2fr 1fr 1fr 1.4fr', gap: 14 }}
            onClick={() => setPicked(r.id)}
            data-testid={`verification-result-${r.checkName}`}
          >
            <span className="name">{r.checkName}</span>
            <span className="dim">{r.groupsCompared.toLocaleString()}</span>
            <span className="status">
              <span className={`dot ${r.differingGroups === 0 ? 'dot-ok' : 'dot-bad'}`} />
              {r.differingGroups.toLocaleString()}
            </span>
            <span className="dim">{new Date(r.completedAtUtc).toLocaleString()}</span>
          </button>
        ))}
      </div>

      {result && <ResultTable result={result} />}
    </div>
  )
}

function ResultTable({ result }: { result: VerificationResult }) {
  const gap = Math.abs(
    new Date(result.targetReadAtUtc).getTime() - new Date(result.sourceReadAtUtc).getTime()) / 1000

  // Groupings first, then each measure's two sides and the difference between them. Built by joining
  // rather than with repeat(): an ungrouped check has no grouping columns, and `repeat(0, …)` is
  // invalid CSS — which invalidates the whole declaration and silently drops the layout.
  const columns = [
    ...result.groupColumns.map(() => 'minmax(80px, 1fr)'),
    ...result.measureColumns.flatMap(() => ['minmax(70px, .8fr)', 'minmax(70px, .8fr)', 'minmax(60px, .6fr)']),
    '110px',
  ].join(' ')

  return (
    <div className="card flush" data-testid="verification-result">
      <div className="card-head">
        <span className="card-title">{result.checkName}</span>
        <span className="card-note" data-testid="verification-read-gap">
          {/* The number a difference has to be weighed against. */}
          read {gap < 1 ? 'less than a second' : `${gap.toFixed(1)}s`} apart ·{' '}
          {result.differenceThreshold > 0
            ? `differences under ${(result.differenceThreshold * 100).toFixed(2)}% are not flagged`
            : 'any difference is flagged'}
        </span>
      </div>

      <div className="grid-head" style={{ gridTemplateColumns: columns, gap: 10 }}>
        {result.groupColumns.map((c) => <span key={c}>{c}</span>)}
        {result.measureColumns.map((m) => (
          <span key={m} style={{ display: 'contents' }}>
            <span>{label(m)} src</span><span>{label(m)} tgt</span><span>Δ</span>
          </span>
        ))}
        <span />
      </div>

      {result.rows.length === 0 && <div className="empty">Nothing to compare.</div>}

      {result.rows.map((row, i) => (
        <Row key={i} row={row} result={result} columns={columns} />
      ))}
    </div>
  )
}

function Row({ row, result, columns }: {
  row: VerificationResultRow
  result: VerificationResult
  columns: string
}) {
  return (
    <div
      className="grid-row"
      style={{ gridTemplateColumns: columns, gap: 10 }}
      data-testid={`verification-row-${row.group.join('-') || 'total'}`}
    >
      {result.groupColumns.map((c, i) => <span key={c} className="mono">{row.group[i] ?? ''}</span>)}

      {result.measureColumns.map((m) => (
        <span key={m} style={{ display: 'contents' }}>
          <span className="dim mono">{format(row.source?.[m])}</span>
          <span className="dim mono">{format(row.target?.[m])}</span>
          {/* Highlighted only when the row is over its threshold. A difference under it is still
              shown as a number — an operator reads it to judge drift — but it is not painted red,
              because a screen where everything is red teaches people to ignore red. */}
          <span
            className="mono"
            style={{ color: row.status === 'Differs' ? 'var(--danger-ink)' : 'var(--ink-8)' }}
          >
            {formatDelta(row.differences[m])}
          </span>
        </span>
      ))}

      <span className="status">
        <span className={`dot ${STATUS_DOT[row.status]}`} />
        {STATUS_LABEL[row.status]}
      </span>
    </div>
  )
}

/** A row count has no column to be named after, so it comes back under a fixed name. */
function label(measure: string) {
  return measure === '__rows' ? 'rows' : measure
}

function format(value: number | undefined) {
  return value === undefined ? '—' : value.toLocaleString(undefined, { maximumFractionDigits: 4 })
}

function formatDelta(value: number | undefined) {
  if (value === undefined) return '—'
  if (value === 0) return '0'
  return (value > 0 ? '+' : '') + value.toLocaleString(undefined, { maximumFractionDigits: 4 })
}
