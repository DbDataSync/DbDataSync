import { Field } from '../../components/Field'
import { useConnections, useDatabases } from '../../api/hooks'
import type { EndpointRef, TaskEndpoints } from '../../api/types'

/**
 * The replication's source and target, inherited by every one of its table mappings.
 *
 * Phase 15 left this card out on the grounds that source and target belong to a mapping. The design
 * was right and that was wrong: a replication moves data between two places, and restating them on
 * every mapping is duplication a mapping may *override* but should not have to repeat.
 */
export function EndpointsCard({ endpoints, mappingCount, onChange }: {
  endpoints: TaskEndpoints
  mappingCount: number | undefined
  onChange: (next: TaskEndpoints) => void
}) {
  return (
    <div className="card" data-testid="endpoints-card">
      <div className="card-head">
        <span className="card-title">Endpoints</span>
        <span className="card-note">
          {mappingCount === undefined
            ? 'inherited by every table mapping in this replication'
            : `inherited by ${mappingCount === 1 ? 'the 1 table mapping' : `all ${mappingCount} table mappings`} unless one overrides it`}
        </span>
      </div>
      <div className="card-body" style={{ display: 'grid', gridTemplateColumns: '1fr 28px 1fr', gap: 14, alignItems: 'center' }}>
        <EndpointFields
          label="Source"
          value={endpoints.source}
          onChange={(source) => onChange({ ...endpoints, source })}
          testIdPrefix="task-source"
        />
        <span style={{ alignSelf: 'end', paddingBottom: 6, textAlign: 'center', font: '400 13px var(--ui)', color: 'var(--ink-faint)' }}>→</span>
        <EndpointFields
          label="Target"
          value={endpoints.target}
          onChange={(target) => onChange({ ...endpoints, target })}
          testIdPrefix="task-target"
        />
      </div>
    </div>
  )
}

function EndpointFields({ label, value, onChange, testIdPrefix }: {
  label: string
  value: EndpointRef | null
  onChange: (next: EndpointRef) => void
  testIdPrefix: string
}) {
  const { data: connections } = useConnections()
  const connectionName = value?.connectionName ?? ''
  const { data: databases } = useDatabases(connectionName || undefined)

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 9 }}>
      <span className="card-title sm">{label}</span>
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
    </div>
  )
}
