import { useState } from 'react'
import { CodeEditor } from '../../components/CodeEditor'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Field } from '../../components/Field'
import { ParameterForm } from '../../components/ParameterForm'
import { useScripts, useTableMappings, useTestSegmentingStrategy } from '../../api/hooks'
import { runsAgainstAConnection } from '../../api/types'
import type {
  SegmentCandidate, SegmentingStrategyConfig, SegmentingStrategyKind,
} from '../../api/types'

const KINDS: { kind: SegmentingStrategyKind; label: string; hint: string }[] = [
  {
    kind: 'DuckDb',
    label: 'DuckDB',
    hint: 'SQL against an in-memory database connected to nothing. Computes the segments from the calendar, touching neither side — free to preview and free to re-run on a schedule.',
  },
  {
    kind: 'SourceSql',
    label: 'Source SQL',
    hint: "SQL in the source's own dialect, run against the source. Segmenting derived from what is actually in the table.",
  },
  {
    kind: 'TargetSql',
    label: 'Target SQL',
    hint: 'The same, against the target — for when the answer is already maintained over there, in a control table or a rollup.',
  },
  {
    kind: 'Script',
    label: 'Script',
    hint: 'A bound C# script. Handed both connections, and free to use either or neither.',
  },
]

/** The slot a segmenting script implements (see ScriptSlots.SegmentingStrategy). */
const SEGMENTING_SLOT = 'segmentingStrategy'

const COLUMNS = '1.4fr 1fr 120px'

/** The four columns every kind has to return — stated once, and offered as the starting point. */
const STARTER_SQL = `-- Every kind returns the same four columns. \`selected\` may be omitted, in
-- which case nothing is pre-selected.
SELECT
    strftime(m, '%Y-%m')                       AS label,
    strftime(m, '%Y-%m-%d')                    AS range_start,
    strftime(m + INTERVAL 1 MONTH, '%Y-%m-%d') AS range_end,
    TRUE                                       AS selected
FROM generate_series(
    DATE '2024-01-01', DATE '2025-01-01', INTERVAL 1 MONTH) AS t(m);`

function blank(): SegmentingStrategyConfig {
  return { name: '', kind: 'DuckDb', sql: STARTER_SQL, scriptName: null, parameters: {} }
}

/**
 * Adds, edits and removes the replication's named segmenting strategies.
 *
 * Phase 58 built strategies end to end and left them authorable only by hand-editing the
 * replication's config file — its own retrospective named the gap. List-with-inline-editor, the shape
 * phase 48's Checks card uses, because `SegmentingStrategyConfig` was deliberately modelled on
 * `VerificationCheckConfig` and an operator who has written a check should recognise this.
 *
 * Saved through the ordinary replication upsert. A strategy lives on the replication, so a second
 * CRUD endpoint would own nothing the existing save does not.
 */
export function SegmentingStrategiesCard({ replicationName, strategies, onChange }: {
  replicationName: string
  strategies: SegmentingStrategyConfig[]
  onChange: (next: SegmentingStrategyConfig[]) => void
}) {
  // `null` is "nothing open"; a number is the index being edited, and -1 is a new one. An index
  // rather than a copy, so Cancel is genuinely a discard.
  const [editing, setEditing] = useState<number | null>(null)
  const [draft, setDraft] = useState<SegmentingStrategyConfig | null>(null)

  const open = (index: number, strategy: SegmentingStrategyConfig) => {
    setEditing(index)
    setDraft(structuredClone(strategy))
  }

  const close = () => { setEditing(null); setDraft(null) }

  const commit = () => {
    if (!draft) return
    onChange(editing === -1 ? [...strategies, draft] : strategies.map((s, i) => (i === editing ? draft : s)))
    close()
  }

  return (
    <div className="card flush" data-testid="segmenting-strategies">
      <div className="card-head tight">
        <span className="card-title sm">Segmenting strategies</span>
        <span className="card-note">
          {strategies.length === 0
            ? 'None yet — a named, reusable way of dividing a table for reload'
            : `${strategies.length} defined on this replication, referenced by name from any of its mappings`}
        </span>
        <button
          type="button"
          className="btn btn-sm spacer"
          onClick={() => open(-1, blank())}
          data-testid="add-strategy-button"
        >
          Add strategy
        </button>
      </div>

      <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 10, height: 29 }}>
        <span>Name</span><span>Kind</span><span />
      </div>

      {strategies.length === 0 && <div className="empty">No strategies defined.</div>}

      {strategies.map((strategy, i) => (
        <div key={i} className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 10 }}>
          <span className="name">{strategy.name}</span>
          <span className="dim">{KINDS.find((k) => k.kind === strategy.kind)?.label ?? strategy.kind}</span>
          <span className="row" style={{ gap: 10, justifySelf: 'end' }}>
            <button
              type="button"
              className="btn-link"
              onClick={() => open(i, strategy)}
              data-testid={`edit-strategy-${strategy.name}`}
            >
              Edit
            </button>
            <button
              type="button"
              className="btn-link quiet"
              onClick={() => onChange(strategies.filter((_, index) => index !== i))}
              data-testid={`remove-strategy-${strategy.name}`}
            >
              Remove
            </button>
          </span>
        </div>
      ))}

      {draft && (
        <StrategyEditor
          replicationName={replicationName}
          draft={draft}
          setDraft={setDraft}
          onCancel={close}
          onCommit={commit}
          isNew={editing === -1}
        />
      )}
    </div>
  )
}

function StrategyEditor({ replicationName, draft, setDraft, onCancel, onCommit, isNew }: {
  replicationName: string
  draft: SegmentingStrategyConfig
  setDraft: (next: SegmentingStrategyConfig) => void
  onCancel: () => void
  onCommit: () => void
  isNew: boolean
}) {
  const { data: scripts } = useScripts()
  const { data: mappings } = useTableMappings(replicationName)
  const test = useTestSegmentingStrategy(replicationName)

  // Which table to test against. A strategy proposes ranges over one table's column, and the two SQL
  // kinds reach their database through that mapping's endpoints — so "test this against nothing in
  // particular" is not a question with an answer. Defaulted to the first, because for the common
  // replication there is only one plausible choice and asking would be a step with no decision in it.
  const [testMapping, setTestMapping] = useState<string | null>(null)
  const mapping = testMapping ?? mappings?.[0] ?? null

  // Scratch state for the Test button only — never part of the strategy being edited. The column is a
  // property of the *table* being tested against, not of the strategy, so it lives here rather than on
  // `draft`; see SegmentingStrategyConfig's own comment for why.
  const [testColumn, setTestColumn] = useState('')

  const set = (patch: Partial<SegmentingStrategyConfig>) => setDraft({ ...draft, ...patch })

  const script = scripts?.find((s) => s.manifest.name === draft.scriptName)?.manifest
  const available = (scripts ?? [])
    .map((s) => s.manifest)
    .filter((m) => m.kind === SEGMENTING_SLOT && m.enabled)

  const candidates: SegmentCandidate[] = test.data?.candidates ?? []
  const kind = KINDS.find((k) => k.kind === draft.kind)

  return (
    <div className="card-body" style={{ borderTop: '1px solid var(--row-edge)' }} data-testid="strategy-editor">
      <div className="form-row">
        <Field label="Name">
          <input
            className="input"
            value={draft.name}
            onChange={(e) => set({ name: e.target.value })}
            placeholder="by-month"
            data-testid="strategy-name-input"
          />
        </Field>
        <Field label="Kind">
          <select
            className="select"
            value={draft.kind}
            onChange={(e) => set({ kind: e.target.value as SegmentingStrategyKind })}
            data-testid="strategy-kind-select"
          >
            {KINDS.map((k) => <option key={k.kind} value={k.kind}>{k.label}</option>)}
          </select>
        </Field>
      </div>

      {kind && <span className="hint">{kind.hint}</span>}

      {/* The picker shows this too, but it matters more here: this is where somebody *creates* the
          thing that will then run unattended on the replication's own schedule, forever. */}
      {runsAgainstAConnection(draft.kind) && (
        <span className="hint warn" data-testid="strategy-connection-note">
          This strategy queries the {draft.kind === 'TargetSql' ? 'target' : 'source'} database, and
          will do so again on every scheduled reload that uses it — not only when it is tested here.
        </span>
      )}

      {draft.kind === 'Script' ? (
        <>
          <Field label="Script">
            <select
              className="select"
              value={draft.scriptName ?? ''}
              onChange={(e) => set({ scriptName: e.target.value || null })}
              data-testid="strategy-script-select"
            >
              <option value="">Pick a script…</option>
              {available.map((m) => <option key={m.name} value={m.name}>{m.name}</option>)}
            </select>
          </Field>
          {available.length === 0 && (
            <span className="hint">
              No enabled script implements a segmenting strategy yet. Scripts are written on the
              Scripts screen.
            </span>
          )}
          {script && (
            <ParameterForm
              parameters={script.parameters ?? []}
              values={draft.parameters ?? {}}
              onChange={(parameters) => set({ parameters })}
              testIdPrefix="strategy-parameters"
            />
          )}
        </>
      ) : (
        <Field label="Query — returns label, range_start, range_end and (optionally) selected">
          <CodeEditor
            value={draft.sql ?? ''}
            language="sql"
            onChange={(sql) => set({ sql })}
            minLines={8}
            maxLines={26}
            testId="strategy-sql-editor"
          />
        </Field>
      )}

      {/* The column is required for every kind but Script, and is not inferable: the query returns
          bounds, not the thing they bound — and it is a property of the table being tested against,
          not of the strategy, so it is asked for here rather than stored on `draft`. */}
      {draft.kind !== 'Script' && (
        <Field label="Column to test against">
          <input
            className="input mono"
            value={testColumn}
            onChange={(e) => setTestColumn(e.target.value)}
            placeholder="OrderDate"
            data-testid="strategy-test-column-input"
          />
        </Field>
      )}

      <div className="row" style={{ gap: 10 }}>
        {/* Testing before saving is the point: an operator should find out a query is malformed while
            writing it, not the next time a scheduled reload silently does nothing. */}
        <button
          type="button"
          className="btn btn-sm"
          disabled={!mapping || test.isPending || (draft.kind !== 'Script' && !testColumn.trim())}
          onClick={() => mapping && test.mutate({ mappingName: mapping, strategy: draft, column: testColumn || null })}
          data-testid="test-strategy-button"
        >
          {test.isPending ? 'Running…' : 'Test'}
        </button>
        {mappings && mappings.length > 1 && (
          <select
            className="select"
            style={{ maxWidth: 240 }}
            value={mapping ?? ''}
            onChange={(e) => setTestMapping(e.target.value)}
            aria-label="Table mapping to test against"
            data-testid="test-strategy-mapping-select"
          >
            {mappings.map((m) => <option key={m} value={m}>{m}</option>)}
          </select>
        )}
        {mappings?.length === 0 && (
          <span className="hint">
            Add a table mapping first — a strategy is tested against one table's column.
          </span>
        )}

        <span className="spacer" />
        <button type="button" className="btn btn-sm" onClick={onCancel} data-testid="cancel-strategy-button">
          Cancel
        </button>
        <button
          type="button"
          className="btn btn-sm btn-primary"
          disabled={!draft.name.trim()}
          onClick={onCommit}
          data-testid="commit-strategy-button"
        >
          {isNew ? 'Add' : 'Update'}
        </button>
      </div>

      <ErrorBanner error={test.error} />

      {test.data && (
        <div className="card" data-testid="strategy-test-result">
          <div className="card-head tight">
            <span className="card-title sm">Proposed segments</span>
            <span className="card-note">
              {candidates.length === 0
                ? 'The query ran and proposed nothing — which unattended means this reloads no rows at all'
                : `${candidates.length} candidate(s) · ${candidates.filter((c) => c.selected).length} selected by default`}
            </span>
          </div>
          {candidates.map((candidate, i) => (
            <div key={i} className="grid-row" style={{ gridTemplateColumns: '1fr 2fr 90px', gap: 10 }}>
              <span className="name">{candidate.label}</span>
              <span className="dim mono">{describe(candidate)}</span>
              <span className="dim" style={{ justifySelf: 'end' }}>
                {candidate.selected ? 'selected' : 'not selected'}
              </span>
            </div>
          ))}
        </div>
      )}

      <span className="hint">
        Strategies are saved with the rest of the replication — <strong>Save settings</strong>, in the
        toolbar, commits them to config history.
      </span>
    </div>
  )
}

/** The bounds this candidate would actually reload. Half-open — min inclusive, max exclusive — which
 * is what makes a strategy's output tile a value space without gaps or overlaps. */
function describe(candidate: SegmentCandidate): string {
  const segment = candidate.segment
  switch (segment.mode) {
    case 'range': return `${segment.column} [${segment.rangeMin}, ${segment.rangeMax})`
    case 'list': return `${segment.column} in ${segment.values.join(', ')}`
    case 'full': return 'the whole table'
    default: return segment.mode
  }
}
