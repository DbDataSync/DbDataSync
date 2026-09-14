import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { AppShell } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { Field } from '../components/Field'
import {
  useDefaultCapabilities,
  useReplication,
  useReplicationLag,
  useReplications,
  useTableMappings,
  useUpsertReplication,
} from '../api/hooks'
import { formatLag } from './replication-detail/lag'
import type { ReplicationTaskConfig, ScheduleMode } from '../api/types'

const COLUMNS = '1.3fr 1fr 1fr .8fr .9fr'

function ScheduleText({ task }: { task: ReplicationTaskConfig | undefined }) {
  if (!task) return <span className="faint">…</span>
  return <>{task.scheduling.mode === 'Continuous' ? `every ${task.scheduling.frequencySeconds}s` : task.scheduling.cronExpression}</>
}

/**
 * The furthest-behind mapping, and nothing else.
 *
 * The Monitoring tab shows the full lowest–highest range; a list row shows only the highest, which
 * is the judgement the plan doc left to whoever laid this out. At list density a range is two
 * numbers in a column narrow enough that neither is legible, and the question a list answers is
 * "which of these needs looking at" — that is the highest, and the low end never changes the answer.
 * The range is one click away on the row it belongs to.
 *
 * `~` when any estimate went into it, for the same reason the tab badges one: the figure carries
 * this system's poll interval as its error and a bare number would not say so.
 */
function LagSummary({ name }: { name: string }) {
  const { data } = useReplicationLag(name)
  if (!data) return <span className="faint">…</span>
  // Null is not zero. No mapping here can report lag — an unsupported reader, or one that has not
  // run — and "0s" would be a claim about the replication that nothing has established.
  if (data.highestLagMs === null) return <span className="faint">no lag data</span>
  return (
    <span title="The furthest behind any of this replication's mappings is. Its full range is on the Monitoring tab.">
      {data.rangeIncludesEstimates ? '~' : ''}{formatLag(data.highestLagMs)}
    </span>
  )
}

function ReplicationRow({ name, onOpen }: { name: string; onOpen: () => void }) {
  const { data } = useReplication(name)
  const { data: mappings } = useTableMappings(name)

  return (
    <button className="grid-row tall" style={{ gridTemplateColumns: COLUMNS, gap: 14 }} onClick={onOpen}>
      <span className="name" style={{ color: 'var(--accent)' }}>{name}</span>
      <span className="dim">{data ? `${data.changeProcessing.reader.kind}` : '…'}</span>
      <span className="dim"><ScheduleText task={data} /></span>
      <span className="dim" data-testid={`replication-lag-${name}`}><LagSummary name={name} /></span>
      <span className="status">
        <span className={`dot ${data?.enabled ? 'dot-ok' : 'dot-idle'}`} />
        {data ? (data.enabled ? 'enabled' : 'disabled') : '…'}
        {mappings && <span className="faint" style={{ marginLeft: 6 }}>· {mappings.length} mapping{mappings.length === 1 ? '' : 's'}</span>}
      </span>
    </button>
  )
}

export function ReplicationsPage() {
  const { data: names, isLoading, error } = useReplications()
  const { data: capabilities } = useDefaultCapabilities()
  const upsert = useUpsertReplication()
  const navigate = useNavigate()

  const [creating, setCreating] = useState(false)
  const [name, setName] = useState('')
  const [mode, setMode] = useState<ScheduleMode>('Continuous')
  const [frequencySeconds, setFrequencySeconds] = useState(60)
  const [cronExpression, setCronExpression] = useState('0 * * * *')

  // The pipeline a new replication starts with comes from the driver's own advertised Kinds — see
  // phase 010. Until a connection exists there is no driver to ask.
  const defaults = capabilities && {
    reader: capabilities.readers[0]?.kind,
    cache: capabilities.stagingProviders[0]?.kind,
    writer: capabilities.writers[0]?.kind,
  }
  const canCreate = !!defaults?.reader && !!defaults.cache && !!defaults.writer

  const create = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!canCreate) return
    const task: ReplicationTaskConfig = {
      name,
      // New replications start disabled — an operator saving one for the first time hasn't necessarily
      // finished configuring mappings/endpoints yet, and shouldn't have it eligible for scheduling
      // before they explicitly turn it on. See phase 69.
      enabled: false,
      scheduling: mode === 'Continuous'
        ? { mode, frequencySeconds, cronExpression: null }
        : { mode, frequencySeconds: null, cronExpression },
      changeProcessing: {
        reader: { kind: defaults!.reader!, options: {} },
        cache: { kind: defaults!.cache!, options: {} },
        writer: { kind: defaults!.writer!, options: {} },
        degreeOfParallelism: 4,
        bulkLoadDegreeOfParallelism: 4,
      },
      // The server's own default (BatchReload reader, cache/writer inherited from changeProcessing) —
      // stated explicitly here only because the type requires the field; a new replication has no
      // reason to want anything else yet.
      bulkLoad: { reader: { kind: 'BatchReload', options: {} }, cache: null, writer: null },
      // Disabled by default, same reasoning as `enabled` above — delete reconciliation (phase 125) is
      // an opt-in an operator turns on once mappings are actually configured.
      reconcile: { enabled: false, every: null, afterChange: { mode: 'none' }, deleteGuard: { mode: 'ratio', maxRatio: 0.5 } },
      // Set on the replication's Overview; a mapping created before they are inherits nothing and
      // has to state its own, which the editor's override toggle covers.
      endpoints: { source: null, target: null },
    }
    await upsert.mutateAsync({ name, task })
    navigate(`/replications/${encodeURIComponent(name)}`)
  }

  return (
    <AppShell crumbs={[{ label: 'Replications' }]}>
      <aside className="sidebar">
        <div className="sidebar-head"><span>Explorer</span></div>
        <div className="sidebar-list">
          {(names ?? []).map((n) => (
            <button key={n} className="sidebar-item" onClick={() => navigate(`/replications/${encodeURIComponent(n)}`)}>
              {n}
            </button>
          ))}
          {names?.length === 0 && <span className="hint" style={{ padding: '6px 7px' }}>No replications yet.</span>}
        </div>
      </aside>

      <div className="pane">
        <div className="page-head">
          <h1 className="page-title">Replications</h1>
          <span className="page-note">
            {names ? `${names.length} task${names.length === 1 ? '' : 's'}` : '…'}
          </span>
          {!creating && (
            <div className="right">
              <button className="btn btn-primary" onClick={() => setCreating(true)} data-testid="new-replication-button">
                New Replication
              </button>
            </div>
          )}
        </div>

        <ErrorBanner error={error ?? upsert.error} />

        <div className="card flush">
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
            <span>Name</span><span>Reader</span><span>Schedule</span><span>Max lag</span><span>Status</span>
          </div>
          {isLoading && <div className="empty">Loading…</div>}
          {names?.length === 0 && <div className="empty">No replications yet.</div>}
          {(names ?? []).map((n) => (
            <ReplicationRow key={n} name={n} onOpen={() => navigate(`/replications/${encodeURIComponent(n)}`)} />
          ))}
        </div>

        {creating && (
          <div className="card" data-testid="new-replication-form">
            <div className="card-head"><span className="card-title">New replication</span></div>
            <div className="card-body">
              <form onSubmit={create} className="stack">
                <Field label="Name">
                  <input
                    className="input"
                    required
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    data-testid="replication-name-input"
                  />
                </Field>

                <div className="form-grid">
                  <Field label="Schedule mode">
                    <select className="select" value={mode} onChange={(e) => setMode(e.target.value as ScheduleMode)}>
                      <option value="Continuous">Continuous</option>
                      <option value="Periodic">Periodic (cron)</option>
                    </select>
                  </Field>
                  {mode === 'Continuous' ? (
                    <Field label="Frequency (seconds)">
                      <input
                        className="input"
                        type="number"
                        min={1}
                        value={frequencySeconds}
                        onChange={(e) => setFrequencySeconds(Number(e.target.value))}
                      />
                    </Field>
                  ) : (
                    <Field label="Cron expression">
                      <input className="input" value={cronExpression} onChange={(e) => setCronExpression(e.target.value)} />
                    </Field>
                  )}
                </div>

                {canCreate ? (
                  <span className="hint">
                    Pipeline defaults to <span className="mono">{defaults!.reader}</span> →{' '}
                    <span className="mono">{defaults!.cache}</span> → <span className="mono">{defaults!.writer}</span>,
                    adjustable on the replication's Overview.
                  </span>
                ) : (
                  <p className="field-error" data-testid="no-capabilities-warning">
                    Add a connection first — which readers, staging providers and writers are available comes from its driver.
                  </p>
                )}

                <div className="row">
                  <button type="submit" className="btn btn-primary" disabled={upsert.isPending || !canCreate} data-testid="create-replication-button">
                    {upsert.isPending ? 'Creating…' : 'Create'}
                  </button>
                  <button type="button" className="btn" onClick={() => setCreating(false)}>Cancel</button>
                </div>
              </form>
            </div>
          </div>
        )}
      </div>
    </AppShell>
  )
}
