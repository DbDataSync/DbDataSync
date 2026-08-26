import { useState } from 'react'

/**
 * Raw JSON editor for a reader/staging/writer's free-form Options map. Deliberately a textarea and not
 * a structured form: Options are driver-defined key/value pairs, so there is no fixed set of fields to
 * build a form out of without teaching this app about every driver — the same reason Kind pickers are
 * fed from the capabilities endpoint.
 *
 * Invalid JSON reports upward as null rather than being swallowed, so the surrounding form can refuse
 * to save. Silently keeping the last valid value would let a Save appear to succeed while discarding
 * whatever was actually typed.
 */
export function JsonOptionsEditor({
  label,
  value,
  onChange,
  testId,
}: {
  label: string
  value: Record<string, string>
  onChange: (next: Record<string, string> | null) => void
  testId?: string
}) {
  const [text, setText] = useState(() => JSON.stringify(value, null, 2))
  const [error, setError] = useState<string | null>(null)

  const edit = (next: string) => {
    setText(next)

    if (next.trim() === '') {
      setError(null)
      onChange({})
      return
    }

    try {
      const parsed = JSON.parse(next)
      if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
        throw new Error('Expected a JSON object of option name to value.')
      }
      // Values reach the API as a Dictionary<string, string>; coercing here keeps a number or boolean
      // typed by hand from failing model binding with an unhelpful message.
      const options = Object.fromEntries(Object.entries(parsed).map(([k, v]) => [k, String(v)]))
      setError(null)
      onChange(options)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Invalid JSON.')
      onChange(null)
    }
  }

  return (
    <div className="form-field">
      <label>{label}</label>
      <textarea
        rows={4}
        className="mono"
        value={text}
        onChange={(e) => edit(e.target.value)}
        data-testid={testId}
        spellCheck={false}
      />
      {error && <p className="field-error">{error}</p>}
    </div>
  )
}
