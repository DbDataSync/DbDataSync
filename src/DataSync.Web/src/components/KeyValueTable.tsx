import { useState } from 'react'

/**
 * The design's Setting / Value table, used for both a pipeline stage's options and a connection's
 * custom properties. It replaces the raw-JSON textarea those used to be: the shape is a string
 * dictionary, so a two-column editor says exactly that and a JSON blob does not.
 *
 * The mockup carries a third "Default" column. Nothing records a default for a driver option or a
 * connection property, so it is omitted rather than filled with a guess.
 */
export function KeyValueTable({ value, onChange, addLabel, testId }: {
  value: Record<string, string>
  onChange: (next: Record<string, string>) => void
  addLabel: string
  testId?: string
}) {
  const [newKey, setNewKey] = useState('')
  const entries = Object.entries(value)

  const setValue = (key: string, next: string) => onChange({ ...value, [key]: next })

  const rename = (from: string, to: string) => {
    if (!to || to === from || to in value) return
    // Rebuilt in order so renaming a key doesn't jump it to the end of the list under the cursor.
    onChange(Object.fromEntries(entries.map(([k, v]) => (k === from ? [to, v] : [k, v]))))
  }

  const remove = (key: string) => onChange(Object.fromEntries(entries.filter(([k]) => k !== key)))

  const add = () => {
    const key = newKey.trim()
    if (!key || key in value) return
    onChange({ ...value, [key]: '' })
    setNewKey('')
  }

  return (
    <div style={{ border: '1px solid var(--card-inner-edge)', borderRadius: 6, overflow: 'hidden' }} data-testid={testId}>
      <div className="grid-head" style={{ gridTemplateColumns: '1.4fr 1fr 70px', gap: 12, padding: '0 12px', height: 29 }}>
        <span>Setting</span><span>Value</span><span />
      </div>

      {entries.length === 0 && <div className="empty" style={{ padding: '14px' }}>None set.</div>}

      {entries.map(([key, entryValue]) => (
        <div key={key} className="grid-row short" style={{ gridTemplateColumns: '1.4fr 1fr 70px', gap: 12, padding: '0 12px' }}>
          <input
            className="input sm"
            defaultValue={key}
            onBlur={(e) => rename(key, e.target.value.trim())}
            aria-label={`${key} name`}
          />
          <input
            className="input sm"
            value={entryValue}
            onChange={(e) => setValue(key, e.target.value)}
            aria-label={`${key} value`}
          />
          <button type="button" className="btn-link quiet" style={{ justifySelf: 'end' }} onClick={() => remove(key)}>
            Remove
          </button>
        </div>
      ))}

      <div className="row" style={{ height: 34, padding: '0 12px', gap: 8 }}>
        <input
          className="input sm"
          style={{ maxWidth: 200 }}
          placeholder={addLabel}
          value={newKey}
          onChange={(e) => setNewKey(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); add() } }}
        />
        <button type="button" className="btn-link" onClick={add} disabled={!newKey.trim()}>+ Add</button>
      </div>
    </div>
  )
}
