/**
 * A setting that either takes a broader level's answer or overrides it.
 *
 * The same three-state shape `MappingSide` renders for connection/database and `ScriptBindings` for a
 * slot: **inherit** (null), or an explicit value of this level's own. Null is not "false" — a mapping
 * that has never been asked about a setting and one that was asked and said no are different
 * configurations, and collapsing them means a replication-level default can never reach a mapping
 * that was saved before the default existed.
 */
export function InheritableToggle({ label, description, value, inherited, onChange, testId }: {
  label: string
  description: string
  /** Null means inherit. */
  value: boolean | null
  /** What the broader level resolves to, shown while inheriting. */
  inherited: boolean
  onChange: (next: boolean | null) => void
  testId: string
}) {
  const overriding = value !== null
  const effective = value ?? inherited

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }} data-testid={testId}>
      <div className="row" style={{ gap: 8 }}>
        <span className="card-title sm">{label}</span>
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

      <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <input
          type="checkbox"
          checked={effective}
          disabled={!overriding}
          onChange={(e) => onChange(e.target.checked)}
          data-testid={`${testId}-checkbox`}
        />
        <span style={{ color: overriding ? 'var(--ink)' : 'var(--ink-8)' }}>
          {effective ? 'On' : 'Off'}
          {!overriding && ' — from the replication'}
        </span>
      </label>

      <span className="hint">{description}</span>
    </div>
  )
}
