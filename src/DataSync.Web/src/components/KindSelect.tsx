export interface KindOption {
  kind: string
  /** Short capability annotation shown beside the name, e.g. "segmentable", "reconciling". */
  note?: string
}

/**
 * A reader/staging/writer Kind picker whose options come from the live capabilities endpoint rather
 * than from a list compiled into this app. A Kind is a driver-advertised identifier, so any list held
 * here would be a guess about which drivers the API happens to have registered.
 *
 * A currently-configured Kind that the driver doesn't advertise is still offered, marked as such:
 * silently dropping it would make the select render some *other* Kind as if it were the saved value,
 * and one wrong Save would then rewrite the config to match the lie.
 */
export function KindSelect({
  label,
  value,
  options,
  onChange,
  testId,
}: {
  label: string
  value: string
  options: KindOption[]
  onChange: (kind: string) => void
  testId?: string
}) {
  const known = options.some((o) => o.kind === value)
  const all: KindOption[] = known || !value ? options : [{ kind: value, note: 'not offered by this driver' }, ...options]

  return (
    <div className="form-field">
      <label>{label}</label>
      <select value={value} onChange={(e) => onChange(e.target.value)} data-testid={testId}>
        {all.length === 0 && <option value="">(no options available)</option>}
        {all.map((o) => (
          <option key={o.kind} value={o.kind}>
            {o.note ? `${o.kind} — ${o.note}` : o.kind}
          </option>
        ))}
      </select>
    </div>
  )
}
