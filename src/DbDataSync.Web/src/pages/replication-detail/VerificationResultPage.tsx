import { useState } from 'react'
import { Link, useOutletContext, useParams } from 'react-router-dom'
import { ErrorBanner } from '../../components/ErrorBanner'
import { useVerificationResult } from '../../api/hooks'
import type { VerificationResultPage as Page, VerificationResultRow, VerificationRowStatus } from '../../api/types'
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

const PAGE_SIZE = 100

/**
 * One check's answer: groupings first, then each measure's two sides and the difference.
 *
 * **A page at a time.** A check over a large table produces a row per group, and this used to be
 * rendered inline on the checks screen — every row, the moment it loaded. Millions of rows meant tens
 * of megabytes of JSON and a DOM node per cell: the tab locked up for minutes and sometimes died. The
 * comparison itself was never the browser's work — the runner does it and writes the answer to
 * parquet — so the fix is to stop shipping the whole answer to show a screenful of it.
 *
 * **Both read times are shown, and that is not decoration.** A replication is behind by design, so a
 * difference is only as meaningful as the gap between the two reads is small — without it an operator
 * is guessing whether they are looking at drift or a defect, which is the thing this screen exists to
 * stop them doing.
 */
export function VerificationResultPage() {
  // The replication comes from the outlet, not from useParams: the route segment is `:name`, and
  // asking for `:replicationName` would quietly hand back undefined.
  const { replicationName, base } = useOutletContext<MappingsOutletContext>()
  const { mappingName, resultId } = useParams<{ mappingName: string; resultId: string }>()

  const [offset, setOffset] = useState(0)
  // Defaults to everything, not to the differences. A check that found nothing is a result worth
  // reading, and a screen that opened filtered would show it as empty.
  const [differingOnly, setDifferingOnly] = useState(false)

  const { data: page, error, isFetching } = useVerificationResult(
    replicationName, Number(resultId), offset, PAGE_SIZE, differingOnly)

  const shown = differingOnly ? page?.differingRows ?? 0 : page?.totalRows ?? 0
  const last = Math.max(0, Math.floor((shown - 1) / PAGE_SIZE) * PAGE_SIZE)

  const filter = (next: boolean) => {
    setDifferingOnly(next)
    // Page 7 of everything is not page 7 of the differences. Going back to the top is the only
    // answer that is not a guess about where they wanted to be.
    setOffset(0)
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }} data-testid="verification-result-page">
      <div className="page-head">
        <h2 className="page-title mono">{page?.checkName ?? '…'}</h2>
        <span className="page-note">{mappingName}</span>
        <div className="right">
          <Link className="btn" to={`${base}/${encodeURIComponent(mappingName!)}/verification`}>
            Back to checks
          </Link>
        </div>
      </div>

      <ErrorBanner error={error} />

      {page && (
        <div className="card flush" data-testid="verification-result">
          <div className="card-head">
            <span className="card-title">{page.checkName}</span>
            <span className="card-note" data-testid="verification-read-gap">
              {/* The number a difference has to be weighed against. */}
              read {readGap(page)} apart ·{' '}
              {page.differenceThreshold > 0
                ? `differences under ${(page.differenceThreshold * 100).toFixed(2)}% are not flagged`
                : 'any difference is flagged'}
            </span>
          </div>

          <div className="row" style={{ height: 38, padding: '0 14px', gap: 12, borderBottom: '1px solid var(--row-edge)' }}>
            <span className="hint" data-testid="verification-result-counts">
              {shown.toLocaleString()} group{shown === 1 ? '' : 's'}
              {differingOnly ? ' differing' : ''} · {page.differingRows.toLocaleString()} of{' '}
              {page.totalRows.toLocaleString()} differ
            </span>

            <span className="row spacer" style={{ gap: 7 }}>
              <button
                type="button"
                className={`toggle ${differingOnly ? 'on' : ''}`}
                aria-pressed={differingOnly}
                onClick={() => filter(!differingOnly)}
                data-testid="differing-only-toggle"
              />
              <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>Differences only</span>
            </span>

            <Pager
              offset={offset}
              size={PAGE_SIZE}
              total={shown}
              last={last}
              busy={isFetching}
              onChange={setOffset}
            />
          </div>

          <div className="grid-head" style={{ gridTemplateColumns: columnsFor(page), gap: 10 }}>
            {page.groupColumns.map((c) => <span key={c}>{c}</span>)}
            {page.measureColumns.map((m) => (
              <span key={m} style={{ display: 'contents' }}>
                <span>{label(m)} src</span><span>{label(m)} tgt</span><span>Δ</span>
              </span>
            ))}
            <span />
          </div>

          {/* The rows scroll inside the card rather than growing the page. A hundred rows is a page
              worth reading, and a page that grows to five thousand pixels puts the pager somewhere
              you have to go looking for it. */}
          <div style={{ overflow: 'auto', maxHeight: '60vh' }}>
            {page.rows.length === 0 && (
              <div className="empty">
                {differingOnly && page.totalRows > 0 ? 'Nothing differs on this check.' : 'Nothing to compare.'}
              </div>
            )}

            {page.rows.map((row, i) => <Row key={offset + i} row={row} page={page} />)}
          </div>
        </div>
      )}
    </div>
  )
}

/**
 * Offset paging, not "load more". A result is an artifact somebody scans; appending to a list forever
 * ends up with the same number of DOM nodes as before, one scroll at a time.
 */
function Pager({ offset, size, total, last, busy, onChange }: {
  offset: number
  size: number
  total: number
  last: number
  busy: boolean
  onChange: (next: number) => void
}) {
  const from = total === 0 ? 0 : offset + 1
  const to = Math.min(offset + size, total)

  return (
    <span className="row" style={{ gap: 8 }}>
      <span className="hint mono" data-testid="verification-page-range">
        {from.toLocaleString()}–{to.toLocaleString()}
      </span>
      <button
        type="button" className="btn btn-sm" disabled={offset === 0 || busy}
        onClick={() => onChange(0)} data-testid="page-first"
      >
        ⇤
      </button>
      <button
        type="button" className="btn btn-sm" disabled={offset === 0 || busy}
        onClick={() => onChange(Math.max(0, offset - size))} data-testid="page-previous"
      >
        ←
      </button>
      <button
        type="button" className="btn btn-sm" disabled={offset >= last || busy}
        onClick={() => onChange(offset + size)} data-testid="page-next"
      >
        →
      </button>
      <button
        type="button" className="btn btn-sm" disabled={offset >= last || busy}
        onClick={() => onChange(last)} data-testid="page-last"
      >
        ⇥
      </button>
    </span>
  )
}

/** Groupings first, then each measure's two sides and the difference between them. Built by joining
 * rather than with repeat(): an ungrouped check has no grouping columns, and `repeat(0, …)` is
 * invalid CSS — which invalidates the whole declaration and silently drops the layout. */
function columnsFor(page: Page) {
  return [
    ...page.groupColumns.map(() => 'minmax(80px, 1fr)'),
    ...page.measureColumns.flatMap(() => ['minmax(70px, .8fr)', 'minmax(70px, .8fr)', 'minmax(60px, .6fr)']),
    '110px',
  ].join(' ')
}

function Row({ row, page }: { row: VerificationResultRow; page: Page }) {
  return (
    <div
      className="grid-row"
      style={{ gridTemplateColumns: columnsFor(page), gap: 10 }}
      data-testid={`verification-row-${row.group.join('-') || 'total'}`}
    >
      {page.groupColumns.map((c, i) => <span key={c} className="mono">{row.group[i] ?? ''}</span>)}

      {page.measureColumns.map((m) => (
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

function readGap(page: Page) {
  const seconds = Math.abs(
    new Date(page.targetReadAtUtc).getTime() - new Date(page.sourceReadAtUtc).getTime()) / 1000
  return seconds < 1 ? 'less than a second' : `${seconds.toFixed(1)}s`
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
