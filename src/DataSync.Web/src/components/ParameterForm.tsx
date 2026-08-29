import { Field } from './Field'
import { KeyValueTable } from './KeyValueTable'
import { isVararg, occurrences, varargEntries, withVararg } from './parameterValues'
import type { ParameterDescriptor } from '../api/types'

/**
 * Renders whatever an author declared.
 *
 * The same relationship `DriverCapabilities` already has with the Kind pickers: the server says what
 * exists, this renders it, and a new driver or script needs no release here to be configurable.
 * Connection extras, pipeline stage settings and script binding parameters are all callers of this
 * now, rather than three separate form implementations that agreed by accident.
 *
 * `KeyValueTable` is the renderer for a `Property` vararg rather than a connection-specific
 * component — which is what it had quietly become.
 */
export function ParameterForm({ parameters, values, onChange, options, testIdPrefix }: {
  parameters: ParameterDescriptor[]
  /** Flat, and stays flat: this is the persisted shape. */
  values: Record<string, string>
  onChange: (next: Record<string, string>) => void
  /** Choices this screen can supply that the declaration cannot — the mapping's columns for a
   * ColumnPicker, the registry's scripts for a script-valued dropdown. Keyed by parameter name. */
  options?: Record<string, string[]>
  testIdPrefix: string
}) {
  // What the declarer currently says applies. No condition logic here and no expression language:
  // the rule belongs to whoever owns the setting, and a second copy of it in this file would be the
  // copy that disagrees.
  const shown = parameters.filter((p) => p.visible !== false)

  if (shown.length === 0)
    return <span className="hint" data-testid={`${testIdPrefix}-none`}>This choice has no settings.</span>

  // Cards keep their declared order — first mention wins — so an author can lay a form out by
  // declaring in the order they want it read.
  const cards = [...new Set(shown.map((p) => p.layout?.card ?? ''))]

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }} data-testid={testIdPrefix}>
      {cards.map((card) => (
        <div key={card} style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {card && <span className="card-title sm">{card}</span>}
          {groupsIn(shown, card).map((group, i) => (
            <div key={i} style={{ display: 'flex', gap: 12, alignItems: 'flex-start' }}>
              {group.map((parameter) => (
                <div key={parameter.name} style={{ flex: parameter.layout?.size ?? 1, minWidth: 0 }}>
                  <Parameter
                    parameter={parameter}
                    values={values}
                    onChange={onChange}
                    options={options?.[parameter.name]}
                    testIdPrefix={testIdPrefix}
                  />
                </div>
              ))}
            </div>
          ))}
        </div>
      ))}
    </div>
  )
}

/** Parameters sharing a group name sit on one row; an ungrouped parameter gets a row of its own,
 * because sharing a row with everything else that declared nothing is not a layout anyone chose. */
function groupsIn(parameters: ParameterDescriptor[], card: string): ParameterDescriptor[][] {
  const inCard = parameters.filter((p) => (p.layout?.card ?? '') === card)
  const rows: ParameterDescriptor[][] = []

  for (const parameter of inCard) {
    const group = parameter.layout?.group ?? ''
    const existing = group ? rows.find((r) => (r[0].layout?.group ?? '') === group) : undefined
    if (existing) existing.push(parameter)
    else rows.push([parameter])
  }

  return rows
}

function Parameter({ parameter, values, onChange, options, testIdPrefix }: {
  parameter: ParameterDescriptor
  values: Record<string, string>
  onChange: (next: Record<string, string>) => void
  options?: string[]
  testIdPrefix: string
}) {
  const label = parameter.label || parameter.name
  const testId = `${testIdPrefix}-${parameter.name}`

  if (isVararg(parameter)) {
    const { min, max } = occurrences(parameter)
    const entries = varargEntries(parameter, values)
    const count = Object.keys(entries).length

    return (
      <Field label={parameter.required ? `${label} *` : label}>
        {parameter.description && <span className="hint">{parameter.description}</span>}
        <KeyValueTable
          value={entries}
          onChange={(next) => onChange(withVararg(parameter, values, next))}
          addLabel={`${label} name`}
          testId={testId}
        />
        {/* The bound, where an operator can see it. A limit nobody is told about is a limit they hit. */}
        {(count < min || count > max) && (
          <span className="hint" style={{ color: 'var(--danger-ink)' }} data-testid={`${testId}-bound`}>
            {count < min ? `At least ${min} needed; ${count} set.` : `At most ${max} allowed; ${count} set.`}
          </span>
        )}
      </Field>
    )
  }

  const value = values[parameter.name] ?? parameter.default ?? ''
  const set = (next: string) => onChange({ ...values, [parameter.name]: next })

  return (
    <Field label={parameter.required ? `${label} *` : label}>
      {parameter.description && <span className="hint">{parameter.description}</span>}
      <Control parameter={parameter} value={value} set={set} options={options} testId={testId} />
      {parameter.required && !value && (
        <span className="hint" style={{ color: 'var(--danger-ink)' }} data-testid={`${testId}-required`}>
          Required.
        </span>
      )}
    </Field>
  )
}

function Control({ parameter, value, set, options, testId }: {
  parameter: ParameterDescriptor
  value: string
  set: (next: string) => void
  options?: string[]
  testId: string
}) {
  // A dropdown's choices come from the declaration when it knows them, and from the screen when it
  // does not — which script names exist is the registry's question, not a driver's.
  const choices = parameter.dropdownOptions ?? options

  switch (parameter.type) {
    case 'Bool':
      return (
        <span className="row" style={{ gap: 7, height: 30 }}>
          <button
            type="button"
            className={`toggle ${value === 'true' ? 'on' : ''}`}
            aria-pressed={value === 'true'}
            onClick={() => set(value === 'true' ? 'false' : 'true')}
            data-testid={testId}
          />
          <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>
            {value === 'true' ? 'On' : 'Off'}
          </span>
        </span>
      )

    case 'Dropdown':
    case 'ColumnPicker':
      return (
        <select className="select" value={value} onChange={(e) => set(e.target.value)} data-testid={testId}>
          <option value="">Select…</option>
          {/* A value the choices no longer offer is kept and labelled, not silently dropped: it is
              what the config says, and losing it on the next save would be a change nobody made. */}
          {value && !(choices ?? []).includes(value) && <option value={value}>{value} — not offered</option>}
          {(choices ?? []).map((c) => (
            <option key={c} value={c}>{parameter.dropdownLabels?.[c] ?? c}</option>
          ))}
        </select>
      )

    case 'Secret':
      return (
        <input
          className="input"
          type="password"
          // Never pre-filled, because the server never sends one back. Blank therefore means "keep
          // what is stored" — the contract the connection password has had since phase 3, now
          // belonging to the type instead of to one hand-written field.
          placeholder="Leave blank to keep existing"
          value={value}
          onChange={(e) => set(e.target.value)}
          data-testid={testId}
        />
      )

    case 'Number':
      return (
        <input
          className="input"
          type="number"
          value={value}
          onChange={(e) => set(e.target.value)}
          data-testid={testId}
        />
      )

    case 'Date':
    case 'DateTime':
      return (
        <input
          className="input"
          type={parameter.type === 'Date' ? 'date' : 'datetime-local'}
          value={value}
          onChange={(e) => set(e.target.value)}
          data-testid={testId}
        />
      )

    default:
      return (
        <input className="input" value={value} onChange={(e) => set(e.target.value)} data-testid={testId} />
      )
  }
}
