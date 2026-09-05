import { INTENT_INFO } from '../pages/replication-detail/readIntent'
import type { ReadIntent } from '../api/types'

/**
 * `DefaultReadIntent`, wherever it is configured — the replication's Pipeline tab and a mapping's own,
 * beside its other overrides. Same inherit/override shape `InheritableToggle` gives a boolean, for an
 * enum: null means inherit, an explicit value overrides.
 *
 * **What it means gets a sentence on screen**, per phase 102: setting this to either `Changes…` value
 * is how an operator asserts that the application must never choose a full load on its own — a
 * brand-new mapping under that setting will never read the rows that predate its feed.
 */
export function ReadIntentSetting({ value, inherited, options, onChange, testId }: {
  /** Null means inherit. */
  value: ReadIntent | null
  /** What the broader level resolves to, shown while inheriting. */
  inherited: ReadIntent
  /** Which values may be chosen — always includes `InitialLoad`; see `readIntent.ts`'s
   * `offeredIntents`. */
  options: ReadIntent[]
  onChange: (next: ReadIntent | null) => void
  testId: string
}) {
  const overriding = value !== null
  const effective = value ?? inherited

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }} data-testid={testId}>
      <div className="row" style={{ gap: 8 }}>
        <span className="card-title sm">Default read intent</span>
        {!overriding && <span className="badge">INHERITED</span>}
        <span className="spacer row" style={{ gap: 7 }}>
          <button
            type="button"
            className={`toggle ${overriding ? 'on' : ''}`}
            // Turning the override on starts from what is currently in effect, so it is a starting
            // point rather than a reset; turning it off drops back to inheriting.
            onClick={() => onChange(overriding ? null : effective)}
            aria-pressed={overriding}
            data-testid={`${testId}-override`}
          />
          <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>Override here</span>
        </span>
      </div>

      <select
        className="select"
        value={effective}
        disabled={!overriding}
        onChange={(e) => onChange(e.target.value as ReadIntent)}
        data-testid={`${testId}-select`}
      >
        {options.map((intent) => (
          <option key={intent} value={intent}>{INTENT_INFO[intent].label}</option>
        ))}
      </select>

      <span className="hint">
        {INTENT_INFO[effective].hint}
        {!overriding && ' — from the replication.'}
      </span>

      <span className="hint">
        What this applies to reads next, before it has ever completed a pass — never consulted again
        once one has. Setting this to either "Changes…" value is how an operator asserts that the
        application must never choose a full load on its own: a brand-new mapping under that setting
        will never read the rows that predate its feed.
      </span>
    </div>
  )
}
