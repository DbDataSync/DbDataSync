import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { AppShell, SectionTabs } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { Field } from '../components/Field'
import { useCompileScript, useDeleteScript, useScript, useScriptSlots, useUpsertScript } from '../api/hooks'
import type { ScriptDefinition } from '../api/types'

const STARTER = `using DataSync.Scripting.Abstractions;

public sealed class MyExpression : ISqlColumnExpression
{
    // Return null to leave the column exactly as it would have been.
    // {{column}} is substituted by the reader with a reference that is correct for
    // its own statement, so never build one from the column name yourself.
    public string? RenderSql(SqlColumnExpressionContext context)
    {
        return $"UPPER({context.ColumnReference})";
    }
}
`

const SQL_STARTER = `-- {{target}}, {{targetSchema}}, {{targetTable}}, {{source}}, {{staging}} are quoted
-- identifiers, substituted textually. @replication, @mapping, @runId, @runKind, @segment,
-- @segmentIndex, @segmentCount, @isLastSegment, @rowsStaged, @rowsWritten, @watermark are bound
-- values. Not every one is available at every hook point — see the phase 26 doc.
UPDATE STATISTICS {{target}};
`

const empty = (slot: string): ScriptDefinition => ({
  manifest: {
    name: '', kind: slot, language: 'CSharp', entryType: 'MyExpression',
    description: '', parameters: [], enabled: true,
  },
  code: STARTER,
})

/**
 * Write a script, compile it, save it. Compilation happens on save too, server-side — a script that
 * will not compile is never stored, so a binding can never name one that cannot run.
 *
 * A plain monospace textarea rather than an embedded editor: the compile diagnostics carry most of what
 * a language service would, and Monaco is a large dependency to take for the rest.
 */
export function ScriptEditPage() {
  const { name } = useParams<{ name: string }>()
  const isNew = !name || name === 'new'
  const navigate = useNavigate()

  const { data: slots } = useScriptSlots()
  const { data: existing } = useScript(isNew ? undefined : name)
  const upsert = useUpsertScript()
  const compile = useCompileScript()
  const del = useDeleteScript()

  const [draft, setDraft] = useState<ScriptDefinition | null>(isNew ? empty(slots?.[0] ?? 'sqlColumnExpression') : null)

  useEffect(() => {
    if (isNew || draft || !existing) return
    setDraft(structuredClone(existing))
  }, [isNew, draft, existing])

  const crumbs = [{ label: 'Scripts', to: '/scripts' }, { label: isNew ? 'new' : name!, mono: true }]

  if (!draft) {
    return (
      <AppShell crumbs={crumbs} tabs={<SectionTabs />}>
        <div className="pane"><span className="hint">Loading…</span></div>
      </AppShell>
    )
  }

  const setManifest = (patch: Partial<ScriptDefinition['manifest']>) =>
    setDraft({ ...draft, manifest: { ...draft.manifest, ...patch } })

  const isSql = draft.manifest.language === 'Sql'

  const setLanguage = (language: 'CSharp' | 'Sql') => {
    if (!isNew) return setManifest({ language }) // Changing language on a saved script is unusual but not forbidden.
    setDraft({
      ...draft,
      manifest: { ...draft.manifest, language, entryType: language === 'Sql' ? null : 'MyExpression' },
      code: language === 'Sql' ? SQL_STARTER : STARTER,
    })
  }

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    await upsert.mutateAsync({ name: draft.manifest.name, script: draft })
    navigate('/scripts')
  }

  // Diagnostics from an explicit Check, or from a save the server refused.
  const diagnostics = compile.data?.diagnostics ?? []

  return (
    <AppShell
      crumbs={crumbs}
      tabs={
        <>
          <SectionTabs />
          <div className="actions">
            <button
              className="btn btn-chrome"
              onClick={() => compile.mutate({ name: draft.manifest.name || 'draft', script: draft })}
              disabled={compile.isPending}
              data-testid="check-script-button"
            >
              {compile.isPending ? (isSql ? 'Validating…' : 'Checking…') : (isSql ? 'Validate' : 'Check')}
            </button>
            <button className="btn btn-chrome" onClick={() => navigate('/scripts')}>Cancel</button>
            <button
              className="btn btn-primary btn-chrome"
              type="submit"
              form="script-form"
              disabled={upsert.isPending || !draft.manifest.name}
              data-testid="save-script-button"
            >
              {upsert.isPending ? 'Saving…' : 'Save'}
            </button>
            {!isNew && (
              <button
                className="btn btn-danger btn-chrome"
                onClick={async () => { await del.mutateAsync(name!); navigate('/scripts') }}
                data-testid={`delete-script-${name}`}
              >
                Delete
              </button>
            )}
          </div>
        </>
      }
    >
      <div className="pane">
        <div className="page-head">
          <h1 className="page-title mono">{isNew ? 'New script' : draft.manifest.name}</h1>
          <span className="badge">{draft.manifest.kind}</span>
        </div>

        <ErrorBanner error={upsert.error} />

        <form id="script-form" onSubmit={save} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div className="card">
            <div className="card-head"><span className="card-title">Script</span></div>
            <div className="card-body">
              <div className="form-grid">
                <Field label="Name">
                  <input
                    className="input mono" required disabled={!isNew}
                    value={draft.manifest.name}
                    onChange={(e) => setManifest({ name: e.target.value })}
                    data-testid="script-name-input"
                  />
                </Field>
                <Field label="Kind">
                  <select
                    className="select"
                    value={draft.manifest.kind}
                    onChange={(e) => setManifest({ kind: e.target.value })}
                    data-testid="script-kind-select"
                  >
                    {(slots ?? [draft.manifest.kind]).map((s) => <option key={s} value={s}>{s}</option>)}
                  </select>
                </Field>
              </div>
              <div className="form-grid">
                <Field label="Language">
                  <select
                    className="select"
                    value={draft.manifest.language}
                    onChange={(e) => setLanguage(e.target.value as 'CSharp' | 'Sql')}
                    disabled={!isNew}
                    data-testid="script-language-select"
                  >
                    <option value="CSharp">C#</option>
                    <option value="Sql">SQL</option>
                  </select>
                </Field>
                {!isSql && (
                  <Field label="Entry type">
                    {/* Named rather than discovered, so a script holding helper types has an
                        unambiguous entry point. */}
                    <input
                      className="input mono" required
                      value={draft.manifest.entryType ?? ''}
                      onChange={(e) => setManifest({ entryType: e.target.value })}
                      data-testid="script-entry-type-input"
                    />
                  </Field>
                )}
                <Field label="Enabled">
                  <select
                    className="select"
                    value={draft.manifest.enabled ? 'yes' : 'no'}
                    onChange={(e) => setManifest({ enabled: e.target.value === 'yes' })}
                  >
                    <option value="yes">Enabled</option>
                    <option value="no">Disabled</option>
                  </select>
                </Field>
              </div>
              <Field label="Description">
                <input
                  className="input"
                  value={draft.manifest.description ?? ''}
                  onChange={(e) => setManifest({ description: e.target.value || null })}
                />
              </Field>
            </div>
          </div>

          <div className="card">
            <div className="card-head">
              <span className="card-title">Code</span>
              <span className="card-note">
                {isSql
                  ? 'SQL, token/parameter-checked on save · no compilation'
                  : 'C#, compiled on save · no file, network or process access'}
              </span>
              {compile.data && (
                <span className="status spacer">
                  <span className={`dot ${compile.data.compiles ? 'dot-ok' : 'dot-bad'}`} />
                  {compile.data.compiles ? (isSql ? 'valid' : 'compiles') : `${diagnostics.length} error(s)`}
                </span>
              )}
            </div>
            <div className="card-body">
              <textarea
                className="input mono"
                spellCheck={false}
                style={{ minHeight: 320, lineHeight: 1.5, resize: 'vertical', whiteSpace: 'pre' }}
                value={draft.code}
                onChange={(e) => setDraft({ ...draft, code: e.target.value })}
                data-testid="script-code-input"
              />
              {diagnostics.length > 0 && (
                <div data-testid="script-diagnostics" style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                  {diagnostics.map((d, i) => (
                    <span key={i} className="mono" style={{ font: '400 11.5px var(--mono)', color: 'var(--danger-ink)' }}>
                      {d.line > 0 ? `(${d.line},${d.column}): ` : ''}{d.message}
                    </span>
                  ))}
                </div>
              )}
            </div>
          </div>
        </form>
      </div>
    </AppShell>
  )
}
