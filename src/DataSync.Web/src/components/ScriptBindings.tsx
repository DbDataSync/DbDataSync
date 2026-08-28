import { Field } from './Field'
import { KeyValueTable } from './KeyValueTable'
import { useScripts, useScriptSlots } from '../api/hooks'
import type { ScriptBinding, ScriptBindings } from '../api/types'

/**
 * Binds scripts to slots at one level of the hierarchy — connection, replication or table mapping.
 *
 * Three states per slot, and they are genuinely three:
 *
 * - **Inherit** — this level says nothing, and whatever a broader level bound applies. The key is
 *   absent from the dictionary.
 * - **A script** — this level binds one, replacing anything inherited, parameters and all.
 * - **None** — this level explicitly binds nothing, *overriding* an inherited script. The key is
 *   present with a null value.
 *
 * The third is why the model is a dictionary rather than a nullable field: "absent" cannot mean both
 * inherit and none. Phase 16's endpoints never needed the distinction because an endpoint is always
 * required; a script never is.
 */
export function ScriptBindingsCard({ bindings, inherited, level, onChange }: {
  bindings: ScriptBindings
  /** What broader levels bind, per slot — for the INHERITED badge. Empty at the connection level. */
  inherited: ScriptBindings
  level: 'connection' | 'replication' | 'mapping'
  onChange: (next: ScriptBindings) => void
}) {
  const { data: slots } = useScriptSlots()
  const { data: scripts } = useScripts()

  const inheritedFrom =
    level === 'mapping' ? 'the replication or connection'
    : level === 'replication' ? 'the source connection'
    : null

  return (
    <div className="card" data-testid="script-bindings-card">
      <div className="card-head">
        <span className="card-title">Scripts</span>
        <span className="card-note">
          {level === 'connection'
            ? 'applied to every replication and mapping that reads through this connection'
            : `inherited from ${inheritedFrom} unless set here`}
        </span>
      </div>
      <div className="card-body">
        {/* Only the slots that mean something at this level. A metadata provider bound on a mapping
            would be invisible to the pickers, which ask before a mapping exists. */}
        {(slots ?? []).filter((s) => s.levels.includes(level)).map(({ slot }) => (
          <SlotBinding
            key={slot}
            slot={slot}
            binding={slot in bindings ? bindings[slot] : undefined}
            inherited={inherited[slot] ?? null}
            available={(scripts ?? []).filter((s) => s.kind === slot && s.enabled).map((s) => s.name)}
            onChange={(next) => {
              const copy = { ...bindings }
              if (next === undefined) delete copy[slot]
              else copy[slot] = next
              onChange(copy)
            }}
          />
        ))}
        {slots?.every((s) => !s.levels.includes(level)) && (
          <span className="hint">No script slots apply at this level.</span>
        )}
      </div>
    </div>
  )
}

const INHERIT = '__inherit__'
const NONE = '__none__'

function SlotBinding({ slot, binding, inherited, available, onChange }: {
  slot: string
  /** undefined: the key is absent, so this level inherits. null: explicitly none. */
  binding: ScriptBinding | null | undefined
  inherited: ScriptBinding | null
  available: string[]
  onChange: (next: ScriptBinding | null | undefined) => void
}) {
  const value = binding === undefined ? INHERIT : binding === null ? NONE : binding.scriptName

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 9 }} data-testid={`script-slot-${slot}`}>
      <div className="row" style={{ gap: 8 }}>
        <span className="card-title sm">{slot}</span>
        {binding === undefined && <span className="badge">INHERITED</span>}
      </div>

      <Field label="Script">
        <select
          className="select"
          value={value}
          onChange={(e) => {
            if (e.target.value === INHERIT) onChange(undefined)
            else if (e.target.value === NONE) onChange(null)
            else onChange({ scriptName: e.target.value, parameters: binding?.parameters ?? {} })
          }}
          data-testid={`script-binding-${slot}`}
        >
          <option value={INHERIT}>
            {inherited ? `Inherit — ${inherited.scriptName}` : 'Inherit — nothing bound'}
          </option>
          <option value={NONE}>None (override)</option>
          {available.map((name) => <option key={name} value={name}>{name}</option>)}
        </select>
      </Field>

      {binding && (
        <Field label="Parameters">
          <KeyValueTable
            value={binding.parameters}
            onChange={(parameters) => onChange({ ...binding, parameters })}
            addLabel="Parameter name"
            testId={`script-parameters-${slot}`}
          />
        </Field>
      )}
    </div>
  )
}
