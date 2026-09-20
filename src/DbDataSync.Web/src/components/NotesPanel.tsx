import { useState } from 'react'
import { useAbout } from '../api/hooks'
import { Markdown } from './Markdown'
import { RichMarkdown } from './RichMarkdown'

/**
 * A Markdown notes field — read by default, edited on request.
 *
 * **Rendered, not an open textarea.** The common visit to this tab is somebody finding out what they
 * are looking at; only occasionally is it somebody writing it down. A field that opens as raw source
 * would optimise for the rare case and make the common one read Markdown syntax by eye. The Edit
 * button is one click away for the rare case.
 *
 * Empty is a distinct state with its own invitation, rather than a blank rectangle: an operator who
 * has never seen this tab should be told what it is for, once.
 */
export function NotesPanel({ value, onChange, subject, testId = 'notes' }: {
  value: string | null | undefined
  onChange: (next: string | null) => void
  /** What these notes are about, for the empty state's prompt — "this replication", "this mapping". */
  subject: string
  testId?: string
}) {
  const [editing, setEditing] = useState(false)
  const text = value ?? ''
  // The deployment's choice, off unless an operator turned it on. Until it is known — and if it cannot be read — the small
  // renderer is used: the safe one is the default, not whatever happens to load first.
  const { data: about } = useAbout()
  const rich = about?.notesRichMarkdown === true

  return (
    <div className="card" data-testid={`${testId}-card`}>
      <div className="card-head">
        <span className="card-title">Notes</span>
        <span className="card-note">Markdown · saved to config history like every other setting</span>
        {rich && (
          // Ambient and persistent, for as long as the setting is on — not a confirmation seen once at toggle time. Notes are
          // stored input rendered in other people's sessions; this is the reminder that they are being read with a richer renderer.
          <span
            className="badge-rich-markdown"
            title="An administrator turned on rich Markdown for Notes on this server (DbDataSync:NotesRichMarkdown). Tables and task lists render; raw HTML is never interpreted, and images show as links."
            data-testid={`${testId}-rich-badge`}
          >
            Rich Markdown on
          </span>
        )}
        <button
          type="button"
          className="btn btn-sm spacer"
          onClick={() => setEditing(!editing)}
          aria-pressed={editing}
          data-testid={`${testId}-edit-toggle`}
        >
          {editing ? 'Done' : text ? 'Edit' : 'Write notes'}
        </button>
      </div>

      <div className="card-body">
        {editing ? (
          <textarea
            className="input notes-editor"
            value={text}
            autoFocus
            placeholder={`What the next person needs to know about ${subject}.`}
            // Empty becomes null rather than "": the config field is nullable, and an empty string
            // would round-trip as a note that exists and says nothing.
            onChange={(e) => onChange(e.target.value.trim() ? e.target.value : null)}
            data-testid={`${testId}-editor`}
          />
        ) : text ? (
          rich
            ? <RichMarkdown text={text} inlineImages={false} testId={`${testId}-rendered`} />
            : <Markdown text={text} testId={`${testId}-rendered`} />
        ) : (
          <span className="hint" data-testid={`${testId}-empty`}>
            Nothing written yet. Notes are for what the next person needs to know about {subject} —
            who owns it, why a setting is the way it is, what broke last time.
          </span>
        )}
      </div>
    </div>
  )
}
