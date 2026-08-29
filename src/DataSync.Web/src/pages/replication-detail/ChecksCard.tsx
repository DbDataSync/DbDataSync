import { useState } from 'react'
import { CodeEditor } from '../../components/CodeEditor'
import { ColumnMultiPicker } from '../../components/ColumnMultiPicker'
import { Field } from '../../components/Field'
import { ParameterForm } from '../../components/ParameterForm'
import { useScripts } from '../../api/hooks'
import type { TableMappingConfig, VerificationCheckConfig, VerificationCheckKind } from '../../api/types'

const KINDS: { kind: VerificationCheckKind; label: string; hint: string }[] = [
  { kind: 'RowCount', label: 'Row count', hint: 'Rows, optionally grouped. The cheapest check and the one that catches the most.' },
  { kind: 'Sum', label: 'Sum', hint: 'Catches what a row count cannot: the right number of rows carrying the wrong values.' },
  { kind: 'Sql', label: 'SQL', hint: 'A statement you write. One for both sides, or one per dialect when the engines differ.' },
  { kind: 'Script', label: 'Script', hint: 'A bound script that generates the statements, for a check that has to read the source first.' },
]

/** The slot a verification script implements (see ScriptSlots.VerificationQueryBuilder). */
const VERIFICATION_SLOT = 'verificationQueryBuilder'

const COLUMNS = '1.1fr .7fr 1.6fr 120px'

function blank(): VerificationCheckConfig {
  return {
    name: '', kind: 'RowCount', groupBy: [], measures: [],
    sourceSql: null, targetSql: null, scriptName: null, parameters: {},
    filter: null, differenceThreshold: 0,
  }
}

/**
 * Adds, edits and removes a mapping's checks.
 *
 * Phase 43 built everything a check does and left it configurable only by API call, which meant the
 * screen that shows results could not produce one. List-with-inline-editor, the shape the column
 * mapping editor uses, because a mapping has a *list* of checks — not the single-slot toggle a script
 * binding is.
 *
 * Saved through the ordinary mapping upsert. A check lives on the mapping, so there is nothing here a
 * second CRUD endpoint would own that the mapping's own save does not already.
 */
export function ChecksCard({ mapping, onSave, saving }: {
  mapping: TableMappingConfig
  onSave: (checks: VerificationCheckConfig[]) => Promise<void>
  saving: boolean
}) {
  const checks = mapping.verification ?? []
  // `null` is "nothing open"; a number is the index being edited, and -1 is a new one. Kept as an
  // index rather than a copy of the check, so Cancel is genuinely a discard.
  const [editing, setEditing] = useState<number | null>(null)
  const [draft, setDraft] = useState<VerificationCheckConfig | null>(null)

  const open = (index: number, check: VerificationCheckConfig) => {
    setEditing(index)
    setDraft(structuredClone(check))
  }

  const commit = async () => {
    if (!draft) return
    const next = editing === -1
      ? [...checks, draft]
      : checks.map((c, i) => (i === editing ? draft : c))
    await onSave(next)
    setEditing(null)
    setDraft(null)
  }

  return (
    <div className="card flush" data-testid="verification-checks">
      <div className="card-head tight">
        <span className="card-title sm">Checks</span>
        <span className="card-note">
          {checks.length === 0
            ? 'None yet — a mapping with no checks has nothing to run'
            : `${checks.length} configured on this mapping`}
        </span>
        <button
          type="button"
          className="btn btn-sm spacer"
          onClick={() => open(-1, blank())}
          data-testid="add-check-button"
        >
          Add check
        </button>
      </div>

      <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 10, height: 29 }}>
        <span>Name</span><span>Kind</span><span>Compares</span><span />
      </div>

      {checks.length === 0 && <div className="empty">No checks configured.</div>}

      {checks.map((check, i) => (
        <div key={i} className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 10 }}>
          <span className="name">{check.name}</span>
          <span className="dim">{KINDS.find((k) => k.kind === check.kind)?.label ?? check.kind}</span>
          <span className="dim" title={summarise(check)}>{summarise(check)}</span>
          <span className="row" style={{ gap: 10, justifySelf: 'end' }}>
            <button type="button" className="btn-link" onClick={() => open(i, check)} data-testid={`edit-check-${check.name}`}>
              Edit
            </button>
            <button
              type="button"
              className="btn-link quiet"
              onClick={() => onSave(checks.filter((_, index) => index !== i))}
              data-testid={`remove-check-${check.name}`}
            >
              Remove
            </button>
          </span>
        </div>
      ))}

      {draft && (
        <CheckEditor
          mapping={mapping}
          draft={draft}
          setDraft={setDraft}
          onCancel={() => { setEditing(null); setDraft(null) }}
          onCommit={commit}
          saving={saving}
          isNew={editing === -1}
        />
      )}
    </div>
  )
}

/** What this check compares, in the terms the results table will use. */
function summarise(check: VerificationCheckConfig): string {
  const measures = check.kind === 'RowCount' ? 'rows' : check.measures.join(', ') || 'nothing yet'
  const grouped = check.groupBy.length > 0 ? ` by ${check.groupBy.join(', ')}` : ' in total'
  return check.kind === 'Script' ? `${check.scriptName ?? 'no script'}${grouped}` : measures + grouped
}

function CheckEditor({ mapping, draft, setDraft, onCancel, onCommit, saving, isNew }: {
  mapping: TableMappingConfig
  draft: VerificationCheckConfig
  setDraft: (next: VerificationCheckConfig) => void
  onCancel: () => void
  onCommit: () => void
  saving: boolean
  isNew: boolean
}) {
  const { data: scripts } = useScripts()
  const columns = mapping.columnMappings.map((m) => m.targetColumn)
  const set = (patch: Partial<VerificationCheckConfig>) => setDraft({ ...draft, ...patch })

  const script = scripts?.find((s) => s.manifest.name === draft.scriptName)?.manifest
  const available = (scripts ?? [])
    .map((s) => s.manifest)
    .filter((m) => m.kind === VERIFICATION_SLOT && m.enabled)

  return (
    <div className="card-body" style={{ borderTop: '1px solid var(--row-edge)' }} data-testid="check-editor">
      <div className="form-row">
        <Field label="Name">
          <input
            className="input"
            value={draft.name}
            placeholder="rows-by-region"
            onChange={(e) => set({ name: e.target.value })}
            data-testid="check-name-input"
          />
        </Field>
        <Field label="Kind">
          <select
            className="select"
            value={draft.kind}
            onChange={(e) => set({ kind: e.target.value as VerificationCheckKind })}
            data-testid="check-kind-select"
          >
            {KINDS.map((k) => <option key={k.kind} value={k.kind}>{k.label}</option>)}
          </select>
        </Field>
      </div>
      <span className="hint">{KINDS.find((k) => k.kind === draft.kind)?.hint}</span>

      {/* Grouping is what makes a result readable — a single total says something differs and nothing
          about where. Every kind takes it, including the hand-written ones, where it describes what
          the statement already returns rather than generating anything. */}
      <Field label="Group by">
        <ColumnMultiPicker
          columns={columns}
          selected={draft.groupBy}
          onChange={(groupBy) => set({ groupBy })}
          testId="check-groupby"
          empty="Ungrouped — one row comparing the whole table."
        />
      </Field>

      {draft.kind !== 'RowCount' && (
        <Field label="Measures">
          <ColumnMultiPicker
            columns={columns}
            selected={draft.measures}
            onChange={(measures) => set({ measures })}
            testId="check-measures"
            empty={draft.kind === 'Sum'
              ? 'A Sum check with no measures compares nothing.'
              : 'What your statement returns besides the grouping columns.'}
          />
        </Field>
      )}

      {draft.kind === 'Sql' && (
        <>
          <Field label="Source SQL">
            <CodeEditor
              value={draft.sourceSql ?? ''}
              language="sql"
              onChange={(sourceSql) => set({ sourceSql: sourceSql || null })}
              minLines={4}
              testId="check-source-sql"
            />
          </Field>
          <Field label="Target SQL">
            <span className="hint">
              Leave blank to run the source's statement on both sides — which is a judgement that the
              two engines are close enough here, not something inferred for you.
            </span>
            <CodeEditor
              value={draft.targetSql ?? ''}
              language="sql"
              onChange={(targetSql) => set({ targetSql: targetSql || null })}
              minLines={4}
              testId="check-target-sql"
            />
          </Field>
        </>
      )}

      {draft.kind === 'Script' && (
        <>
          <Field label="Script">
            <select
              className="select"
              value={draft.scriptName ?? ''}
              onChange={(e) => set({ scriptName: e.target.value || null, parameters: {} })}
              data-testid="check-script-select"
            >
              <option value="">Select…</option>
              {available.map((m) => <option key={m.name} value={m.name}>{m.name}</option>)}
            </select>
            {available.length === 0 && (
              <span className="hint">No enabled script implements the verification slot yet.</span>
            )}
          </Field>
          {script && (
            <ParameterForm
              parameters={script.parameters}
              values={draft.parameters}
              onChange={(parameters) => set({ parameters })}
              testIdPrefix="check-script-parameters"
            />
          )}
        </>
      )}

      <Field label="Filter">
        <span className="hint">
          A SQL predicate narrowing both sides — the same shape the mapping's own source filter takes.
        </span>
        <CodeEditor
          value={draft.filter ?? ''}
          language="sql"
          onChange={(filter) => set({ filter: filter || null })}
          minLines={2}
          testId="check-filter"
        />
      </Field>

      <Field label="Difference threshold (%)">
        <span className="hint">
          How far apart the two sides may be before it is flagged, as a percentage of the larger side.
          Zero flags any difference — which for a replication that is behind by design means flagging
          drift as a defect.
        </span>
        <input
          className="input"
          type="number"
          min={0}
          step="0.01"
          // Stored as a fraction and shown as a percentage, because that is how the results card
          // already speaks about it — two spellings of the same number in one screen would be worse.
          value={draft.differenceThreshold === 0 ? '0' : String(draft.differenceThreshold * 100)}
          onChange={(e) => set({ differenceThreshold: (Number(e.target.value) || 0) / 100 })}
          data-testid="check-threshold-input"
        />
      </Field>

      <div className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
        <button type="button" className="btn" onClick={onCancel}>Cancel</button>
        <button
          type="button"
          className="btn btn-primary"
          disabled={saving || !draft.name.trim()}
          onClick={onCommit}
          data-testid="save-check-button"
        >
          {saving ? 'Saving…' : isNew ? 'Add check' : 'Save check'}
        </button>
      </div>
    </div>
  )
}
