import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { AppShell, SectionTabs } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { Field } from '../components/Field'
import {
  useDefaultCapabilities,
  useReplication,
  useReplications,
  useTableMappings,
  useUpsertReplication,
} from '../api/hooks'
import type { ReplicationTaskConfig, ScheduleMode } from '../api/types'

const COLUMNS = '1.3fr 1fr 1fr .9fr'

function ScheduleText({ task }: { task: ReplicationTaskConfig | undefined }) {
  if (!task) return <span className="faint">…</span>
  return <>{task.scheduling.mode === 'Continuous' ? `every ${task.scheduling.frequencySeconds}s` : task.scheduling.cronExpression}</>
}

function ReplicationRow({ name, onOpen }: { name: string; onOpen: () => void }) {
  const { data } = useReplication(name)
  const { data: mappings } = useTableMappings(name)

  return (
    <button className="grid-row tall" style={{ gridTemplateColumns: COLUMNS, gap: 14 }} onClick={onOpen}>
      <span className="name" style={{ color: 'var(--accent)' }}>{name}</span>
      <span className="dim">{data ? `${data.changeProcessing.reader.kind}` : '…'}</span>
      <span className="dim"><ScheduleText task={data} /></span>
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
      enabled: true,
      scheduling: mode === 'Continuous'
        ? { mode, frequencySeconds, cronExpression: null }
        : { mode, frequencySeconds: null, cronExpression },
      changeProcessing: {
        reader: { kind: defaults!.reader!, parallelism: 1, options: {} },
        cache: { kind: defaults!.cache!, options: {} },
        writer: { kind: defaults!.writer!, parallelism: 1, options: {} },
      },
    }
    await upsert.mutateAsync({ name, task })
    navigate(`/replications/${encodeURIComponent(name)}`)
  }

  return (
    <AppShell crumbs={[{ label: 'Replications' }]} tabs={<SectionTabs active="replications" />}>
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
          <span className="page-title">Replications</span>
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
            <span>Name</span><span>Reader</span><span>Schedule</span><span>Status</span>
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
