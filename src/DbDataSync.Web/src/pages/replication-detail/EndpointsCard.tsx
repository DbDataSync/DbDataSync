import { Field } from '../../components/Field'
import { EndpointSideCard, EndpointSidePair } from '../../components/EndpointSidePair'
import { useConnections, useDatabases } from '../../api/hooks'
import type { EndpointRef, TaskEndpoints } from '../../api/types'

/**
 * The replication's source and target, inherited by every one of its table mappings.
 *
 * Phase 15 left this card out on the grounds that source and target belong to a mapping. The design
 * was right and that was wrong: a replication moves data between two places, and restating them on
 * every mapping is duplication a mapping may *override* but should not have to repeat.
 *
 * Two cards rather than one, since phase 44 — the same pair the mapping editor renders, from the same
 * component. The "inherited by N mappings" note sits on **both** rather than in a shared header,
 * because a mapping overrides source and target independently: whether *this* side is the one being
 * inherited is genuinely per-side information.
 */
export function EndpointsCard({ endpoints, mappingCount, onChange }: {
  endpoints: TaskEndpoints
  mappingCount: number | undefined
  onChange: (next: TaskEndpoints) => void
}) {
  const note = mappingCount === undefined
    ? 'inherited by every table mapping'
    : `inherited by ${mappingCount === 1 ? 'the 1 mapping' : `all ${mappingCount} mappings`} unless overridden`

  return (
    <div data-testid="endpoints-card">
      <EndpointSidePair
        source={
          <EndpointSideCard side="source" title="Source" head={<span className="card-note">{note}</span>}>
            <EndpointFields
              value={endpoints.source}
              onChange={(source) => onChange({ ...endpoints, source })}
              testIdPrefix="task-source"
            />
          </EndpointSideCard>
        }
        target={
          <EndpointSideCard side="target" title="Target" head={<span className="card-note">{note}</span>}>
            <EndpointFields
              value={endpoints.target}
              onChange={(target) => onChange({ ...endpoints, target })}
              testIdPrefix="task-target"
            />
          </EndpointSideCard>
        }
      />
    </div>
  )
}

function EndpointFields({ value, onChange, testIdPrefix }: {
  value: EndpointRef | null
  onChange: (next: EndpointRef) => void
  testIdPrefix: string
}) {
  const { data: connections } = useConnections()
  const connectionName = value?.connectionName ?? ''
  const { data: databases } = useDatabases(connectionName || undefined)

  return (
    <div className="form-grid">
      <Field label="Connection">
        <select
          className="select"
          value={connectionName}
          onChange={(e) => onChange({ connectionName: e.target.value || null, database: null })}
          data-testid={`${testIdPrefix}-connection-select`}
        >
          <option value="">Select…</option>
          {connections?.map((c) => <option key={c.name} value={c.name}>{c.name}</option>)}
        </select>
      </Field>
      <Field label="Database">
        <select
          className="select"
          value={value?.database ?? ''}
          disabled={!connectionName}
          onChange={(e) => onChange({ connectionName: connectionName || null, database: e.target.value || null })}
          data-testid={`${testIdPrefix}-database-select`}
        >
          <option value="">Select…</option>
          {databases?.map((d) => <option key={d} value={d}>{d}</option>)}
        </select>
      </Field>
    </div>
  )
}
