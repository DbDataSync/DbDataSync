import { useEffect, useRef, useState } from 'react'

interface Props {
  /** The current value, or null/empty when nothing is set. */
  value: string | null | undefined

  /**
   * What to show when `value` is empty — the inferred answer, the default, the thing that happens if
   * nobody intervenes. Rendered faintly, so "this is what it will be" and "this is what I chose" are
   * distinguishable at a glance rather than only by reading.
   */
  placeholder?: string | null

  /** Committed on blur or Enter. An empty string is normalised to null: clearing an override means
   * going back to the inferred value, not storing a blank one. */
  onChange: (next: string | null) => void

  /** Named for a screen reader and for tests, since the pencil itself says nothing about what it edits. */
  label: string

  monospace?: boolean
  testId?: string
}

/**
 * A value shown as text with a pencil beside it; clicking the pencil swaps it for an input.
 *
 * The column mapping editor needs this three times over (target column name, target type, transform)
 * and the reason is the same each time: these are fields that are *usually* right and occasionally
 * overridden. A row of permanently-open inputs reads as a form to fill in, when the truthful message
 * is "this is already decided — change it if you disagree". Text-until-clicked says that, and keeps
 * a forty-column table legible.
 *
 * Escape reverts rather than committing: a pencil that discards edits on blur but keeps them on
 * Escape would be exactly backwards, and this is the one interaction where a mistake is silent.
 */
export function EditableValue({ value, placeholder, onChange, label, monospace, testId }: Props) {
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState(value ?? '')
  const input = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (editing) input.current?.focus()
  }, [editing])

  const commit = () => {
    setEditing(false)
    const next = draft.trim()
    if (next !== (value ?? '')) onChange(next === '' ? null : next)
  }

  if (editing) {
    return (
      <input
        ref={input}
        className={`input sm${monospace ? ' mono' : ''}`}
        value={draft}
        placeholder={placeholder ?? undefined}
        aria-label={label}
        data-testid={testId}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') { e.preventDefault(); commit() }
          if (e.key === 'Escape') { setDraft(value ?? ''); setEditing(false) }
        }}
      />
    )
  }

  const shown = value ?? ''
  return (
    <span className="editable-value">
      <span className={monospace ? 'mono' : undefined} data-testid={testId ? `${testId}-text` : undefined}>
        {shown || <span className="faint">{placeholder ?? '—'}</span>}
      </span>
      <button
        type="button"
        className="pencil"
        title={`Edit ${label}`}
        aria-label={`Edit ${label}`}
        data-testid={testId ? `${testId}-edit` : undefined}
        onClick={() => { setDraft(shown); setEditing(true) }}
      >
        ✎
      </button>
    </span>
  )
}
