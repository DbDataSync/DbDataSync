import { useState } from 'react'
import { Field } from './Field'
import { KeyValueTable } from './KeyValueTable'
import { useScripts, useScriptSlots } from '../api/hooks'
import type { ScriptBinding, ScriptBindings, ScriptSlotInfo } from '../api/types'

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
 *
 * **Collapsed until something is bound.** This is an advanced customisation most installations never
 * touch, and a card reading "no scripts bound" holding a screen's best space is noise for everyone it
 * does not apply to. Collapsed, it is one line saying the feature exists — which is what someone
 * looking for it needs, and all it owes anyone else. It opens by itself when this level binds
 * something, because then it is describing behaviour rather than offering it.
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

  const applicable = (slots ?? []).filter((s) => s.levels.includes(level))
  const boundHere = applicable.filter((s) => s.slot in bindings)
  const [expanded, setExpanded] = useState(false)
  const open = expanded || boundHere.length > 0

  const inheritedFrom =
    level === 'mapping' ? 'the replication or connection'
    : level === 'replication' ? 'the source connection'
    : null

  return (
    <div className="card" data-testid="script-bindings-card">
      <div className="card-head">
        <button
          type="button"
          className="btn-link"
          onClick={() => setExpanded(!open)}
          aria-expanded={open}
          data-testid="script-bindings-toggle"
        >
          {open ? '▾' : '▸'} Custom transforms and providers
        </button>
        <span className="card-note">
          {boundHere.length > 0
            ? `${summarise(boundHere, bindings)} · ${level === 'connection'
                ? 'applied wherever this connection is read'
                : `overriding ${inheritedFrom}`}`
            : level === 'connection'
              ? 'C# that runs for every replication reading through this connection — none bound'
              : `C# that changes what this level reads or writes — none bound, inherited from ${inheritedFrom}`}
        </span>
      </div>
      {/* Not rendered rather than `hidden`: `.card-body` sets `display: flex`, and a class rule beats
          the user-agent's `[hidden]` rule, so the attribute alone shows a collapsed card's contents. */}
      {open && <div className="card-body">
        {/* Only the slots that mean something at this level. A metadata provider bound on a mapping
            would be invisible to the pickers, which ask before a mapping exists. */}
        {applicable.map(({ slot, label, description }) => (
          <SlotBinding
            key={slot}
            slot={slot}
            label={label}
            description={description}
            binding={slot in bindings ? bindings[slot] : undefined}
            inherited={inherited[slot] ?? null}
            available={(scripts ?? []).map((s) => s.manifest).filter((m) => m.kind === slot && m.enabled).map((m) => m.name)}
            onChange={(next) => {
              const copy = { ...bindings }
              if (next === undefined) delete copy[slot]
              else copy[slot] = next
              onChange(copy)
            }}
          />
        ))}
        {slots && applicable.length === 0 && (
          <span className="hint">No script slots apply at this level.</span>
        )}
      </div>}
    </div>
  )
}

const INHERIT = '__inherit__'
const NONE = '__none__'

/** "Row transform, value transform" rather than a count: the point of the collapsed line is to say
 * what is happening, and two names are shorter than "2 scripts bound" is uninformative. */
function summarise(boundHere: ScriptSlotInfo[], bindings: ScriptBindings): string {
  const named = boundHere.map((s) => (bindings[s.slot] === null ? `${s.label} (none)` : s.label))
  return named.join(', ')
}

function SlotBinding({ slot, label, description, binding, inherited, available, onChange }: {
  slot: string
  label: string
  description: string
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
        <span className="card-title sm">{label}</span>
        {binding === undefined && <span className="badge">INHERITED</span>}
      </div>
      {description && <span className="hint">{description}</span>}

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
