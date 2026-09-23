import { useEffect, useState } from 'react'
import { useOutletContext, useParams } from 'react-router-dom'
import { CodeEditor } from '../../components/CodeEditor'
import { ErrorBanner } from '../../components/ErrorBanner'
import { useMappingPreview } from '../../api/hooks'
import type { PreviewOrigin, PreviewStatement } from '../../api/types'
import type { MappingsOutletContext } from './TableMappingsPanel'
import { SubTabs } from '../../components/SubTabs'
import { useMappingTabs } from './mappingTabs'
import { SavedMappingHeading } from './SavedMappingHeading'

const ORIGIN_LABEL: Record<PreviewOrigin, string> = {
  BuiltIn: 'built in',
  OperatorSql: 'your SQL',
  Script: 'script',
}

/**
 * Everything a pass would run for one table mapping, in the order it would run it.
 *
 * A mapping's behaviour is spread across a literal transform per column, a script that generates more
 * of them, transforms that run in this process, four hook points, a hook generator, and the
 * statements the reader, staging provider and writer build themselves. Until this screen there was no
 * way to see any of it without running a pass and reading the log.
 *
 * Read-only, deliberately. The place to change a statement is the thing that generated it, and an
 * editable preview would invite the question of what happens to the generator.
 */
export function MappingPreview() {
  const { replicationName, base } = useOutletContext<MappingsOutletContext>()
  const { mappingName } = useParams<{ mappingName: string }>()
  const { data, isLoading, error } = useMappingPreview(replicationName, mappingName)
  const tabs = useMappingTabs(replicationName, base, mappingName)

  const stages = [...new Set((data?.statements ?? []).map((s) => s.stage))]

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }} data-testid="mapping-preview">
      <div className="page-head">
        <h2 className="page-title mono">{mappingName}</h2>
        {/* Every mapping this screen can be open for is a saved one, so the badge the editor shows
            beside the name is true here too, and was simply missing. */}
        <span className="badge badge-accent">MAPPED</span>
        <span className="page-note">what a pass would run, in order — nothing here is executed</span>
      </div>

      {/* And the same heading: which two tables this is about, read from the mapping as saved. */}
      <SavedMappingHeading replicationName={replicationName} mappingName={mappingName} />

      {/* The same bar the editor wears. These two routes sit beside the editor rather than inside it
          — both are about the mapping *as saved*, which is not what an unsaved editor is showing —
          so the bar is rendered here too rather than hoisted into a shared layout that would drag
          the editor's draft along with it. */}
      <SubTabs
        base={`${base}/${encodeURIComponent(mappingName!)}`}
        tabs={tabs}
        testId="mapping-subtabs"
      />

      <ErrorBanner error={error} />

      {/* Said plainly rather than left to be discovered: a preview is exactly where an operator would
          want to find out that this mapping cannot run. */}
      {data?.problems.map((problem, i) => (
        <div key={i} className="banner warn" role="alert" data-testid="preview-problem">{problem}</div>
      ))}

      {isLoading && <div className="empty">Building the preview…</div>}
      {data?.statements.length === 0 && <div className="empty">Nothing to preview.</div>}

      {stages.map((stage) => (
        <div className="card" key={stage} data-testid={`preview-stage-${stage.replace(/\s+/g, '-').toLowerCase()}`}>
          <div className="card-head">
            <span className="card-title">{stage}</span>
          </div>
          <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {data!.statements.filter((s) => s.stage === stage).map((statement, i) => (
              <Statement key={i} statement={statement} />
            ))}
          </div>
        </div>
      ))}
    </div>
  )
}

function Statement({ statement }: { statement: PreviewStatement }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div className="row" style={{ gap: 8 }}>
        <span className="card-title sm">{statement.title}</span>
        <span className={`badge ${statement.origin === 'BuiltIn' ? '' : 'badge-accent'}`}>
          {ORIGIN_LABEL[statement.origin]}
        </span>
      </div>

      {statement.detail && <span className="hint">{statement.detail}</span>}

      {/* Shown ahead of the statement, not folded into it: these are the exact values this pass would
          bind right now, declared as variables rather than left as the bare placeholders the statement
          itself shows. Pasting this block and the statement below it into a query tool reproduces
          exactly what a pass would run — pasting the statement alone does not, because nothing has
          declared what its parameters mean. */}
      {statement.declaredParameters && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
          <span className="hint">Declared as variables, so pasting this and the statement below reproduces exactly what a pass would run:</span>
          <CodeEditor
            value={statement.declaredParameters}
            language="sql"
            readOnly
            onChange={() => {}}
            minLines={1}
            maxLines={8}
            testId={`preview-params-${statement.title.slice(0, 24)}`}
          />
        </div>
      )}

      {/* A step with no SQL is named and left at that. Rendering an empty editor for one would suggest
          there is a statement that failed to load. */}
      {statement.sql && (
        <CodeEditor
          value={statement.sql}
          language="sql"
          readOnly
          onChange={() => {}}
          minLines={2}
          maxLines={24}
          testId={`preview-sql-${statement.title.slice(0, 24)}`}
        />
      )}

      {statement.columnExpressions && statement.columnExpressions.length > 0 && (
        <ColumnExpressionsDialog expressions={statement.columnExpressions} />
      )}
    </div>
  )
}

/**
 * The generated-expression table, behind a popup rather than open on the page — a mapping with a
 * hundred-plus columns made an inline list of "Column → expression" the loudest, least readable part
 * of the whole preview. `.modal.diff` rather than `.modal.wide`: the same "a narrow box makes code
 * unreadable" reasoning that width already exists for, and an expression can run considerably longer
 * than a diff line.
 */
function ColumnExpressionsDialog({ expressions }: { expressions: { column: string; expression: string }[] }) {
  const [open, setOpen] = useState(false)

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open])

  return (
    <>
      <button
        type="button"
        className="btn btn-sm"
        style={{ alignSelf: 'flex-start' }}
        onClick={() => setOpen(true)}
        data-testid="preview-column-expressions-open"
      >
        View {expressions.length} generated expression{expressions.length === 1 ? '' : 's'}
      </button>

      {open && (
        <div
          className="modal-backdrop"
          onMouseDown={(e) => { if (e.target === e.currentTarget) setOpen(false) }}
        >
          <div
            className="modal diff"
            role="dialog"
            aria-modal="true"
            aria-label="Generated column expressions"
            data-testid="preview-column-expressions-dialog"
          >
            <div style={{ display: 'flex', flexDirection: 'column', gap: 10, padding: 16 }}>
              <div className="row" style={{ justifyContent: 'space-between' }}>
                <span className="card-title">Generated column expressions</span>
                <button type="button" className="btn btn-sm" onClick={() => setOpen(false)}>Close</button>
              </div>
              <div style={{ overflow: 'auto', maxHeight: '70vh', border: '1px solid var(--row-edge)', borderRadius: 4 }}>
                <table className="preview-grid" style={{ borderCollapse: 'collapse', width: '100%' }}>
                  <thead>
                    <tr>
                      <th
                        style={{
                          textAlign: 'left', padding: '5px 9px', whiteSpace: 'nowrap', width: '22%',
                          font: '600 11.5px var(--ui)', color: 'var(--ink-4)',
                          borderBottom: '1px solid var(--row-edge)', background: 'var(--sunken)',
                        }}
                      >
                        Column
                      </th>
                      <th
                        style={{
                          textAlign: 'left', padding: '5px 9px', whiteSpace: 'nowrap',
                          font: '600 11.5px var(--ui)', color: 'var(--ink-4)',
                          borderBottom: '1px solid var(--row-edge)', background: 'var(--sunken)',
                        }}
                      >
                        Expression
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {expressions.map((e) => (
                      <tr key={e.column}>
                        <td
                          style={{
                            padding: '4px 9px', whiteSpace: 'nowrap', verticalAlign: 'top',
                            font: '12px var(--mono)', borderBottom: '1px solid var(--row-edge)', color: 'var(--ink-2)',
                          }}
                        >
                          {e.column}
                        </td>
                        <td
                          style={{
                            padding: '4px 9px', whiteSpace: 'pre-wrap', wordBreak: 'break-word',
                            font: '12px var(--mono)', borderBottom: '1px solid var(--row-edge)', color: 'var(--ink-2)',
                          }}
                        >
                          {e.expression}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          </div>
        </div>
      )}
    </>
  )
}
