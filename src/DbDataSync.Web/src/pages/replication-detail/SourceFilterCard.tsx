import { useState } from 'react'
import { Field } from '../../components/Field'
import { CodeEditor } from '../../components/CodeEditor'

/**
 * The mapping's source filter — its own row beneath both sides rather than stacked under Source
 * alone. It belongs to the source, but hanging it off one card made the two sides different heights
 * and stopped them being comparable at a glance, which is the whole reason to put them next to each
 * other.
 *
 * **Collapsed when empty**: most mappings read the whole table, and a card holding an editor for a
 * predicate nobody wrote is space spent on the exception. It opens by itself when there *is* a filter, because then it is describing behaviour
 * rather than offering it — and says so on the collapsed heading, since a filter silently narrowing
 * what a replication reads is exactly the thing somebody debugging missing rows needs to see first.
 */
export function SourceFilterCard({ filter, onChange }: {
  filter: string | null
  onChange: (next: string | null) => void
}) {
  const [expanded, setExpanded] = useState(false)
  const applied = !!filter?.trim()
  const open = expanded || applied

  return (
    <div className="card" data-testid="source-filter-card">
      <div className="card-head">
        <button
          type="button"
          className="btn-link"
          onClick={() => setExpanded(!open)}
          aria-expanded={open}
          data-testid="source-filter-toggle"
        >
          {open ? '▾' : '▸'} Source filter
        </button>
        {applied
          ? <span className="badge badge-accent" data-testid="source-filter-applied-pill">FILTERS APPLIED</span>
          : <span className="card-note">optional SQL predicate — the whole table is read without one</span>}
      </div>
      {open && (
        <div className="card-body">
          <Field label="Source filter — optional SQL predicate">
            {/* An editor rather than an input: a predicate that narrows a real table outgrows forty
                visible characters quickly, and this one is spliced into the reader's WHERE clause
                verbatim. */}
            <CodeEditor
              value={filter ?? ''}
              language="sql"
              onChange={(next) => onChange(next.trim() ? next : null)}
              minLines={2}
              maxLines={8}
              testId="source-filter-editor"
            />
          </Field>
        </div>
      )}
    </div>
  )
}

