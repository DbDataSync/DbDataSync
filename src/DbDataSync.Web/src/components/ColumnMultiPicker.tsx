/**
 * Picks any number of a mapping's columns, by their **target** names.
 *
 * Chips rather than a multi-select listbox or a checkbox dropdown: what is selected has to be
 * readable without opening anything, because these choices decide what a verification result's rows
 * even mean — and a `<select multiple>` shows a scrolling box where two of eleven items happen to be
 * highlighted, which is the least legible way to say "these two".
 *
 * Target names because that is how a check is described end to end (see `VerificationCheckConfig`):
 * an aliased column is one selection here and the source's name is derived through the mapping,
 * rather than two spellings for an operator to keep in step.
 */
export function ColumnMultiPicker({ columns, selected, onChange, testId, empty }: {
  columns: string[]
  selected: string[]
  onChange: (next: string[]) => void
  testId: string
  /** What to say when nothing is picked, since for these fields that is a meaningful answer. */
  empty: string
}) {
  const toggle = (column: string) => onChange(
    selected.includes(column) ? selected.filter((c) => c !== column) : [...selected, column])

  return (
    <div className="chips" data-testid={testId}>
      {columns.map((column) => (
        <button
          key={column}
          type="button"
          className={`chip ${selected.includes(column) ? 'on' : ''}`}
          aria-pressed={selected.includes(column)}
          onClick={() => toggle(column)}
          data-testid={`${testId}-${column}`}
        >
          {column}
        </button>
      ))}
      {/* A column the mapping no longer has is kept and marked rather than dropped: it is what the
          check says, and losing it on the next save would be a change nobody made. */}
      {selected.filter((c) => !columns.includes(c)).map((column) => (
        <button
          key={column}
          type="button"
          className="chip on unknown"
          title="This column is not in the mapping."
          onClick={() => toggle(column)}
          data-testid={`${testId}-${column}`}
        >
          {column} — not mapped
        </button>
      ))}
      {columns.length === 0 && <span className="hint">No columns mapped yet.</span>}
      {selected.length === 0 && columns.length > 0 && <span className="hint">{empty}</span>}
    </div>
  )
}
