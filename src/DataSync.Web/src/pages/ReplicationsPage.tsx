import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { ErrorBanner } from '../components/ErrorBanner'
import { useReplication, useReplications, useUpsertReplication } from '../api/hooks'
import type { ReplicationTaskConfig, ScheduleMode } from '../api/types'
import { READER_KINDS, CACHE_KINDS, WRITER_KINDS } from '../driverKinds'

function ReplicationRow({ name }: { name: string }) {
  const { data } = useReplication(name)
  return (
    <tr>
      <td>
        <Link to={`/replications/${encodeURIComponent(name)}`}>{name}</Link>
      </td>
      <td>{data ? (data.enabled ? 'Enabled' : 'Disabled') : '…'}</td>
      <td>
        {data
          ? data.scheduling.mode === 'Continuous'
            ? `Every ${data.scheduling.frequencySeconds}s`
            : data.scheduling.cronExpression
          : '…'}
      </td>
    </tr>
  )
}

export function ReplicationsPage() {
  const { data: names, isLoading, error } = useReplications()
  const upsert = useUpsertReplication()
  const navigate = useNavigate()
  const [creating, setCreating] = useState(false)
  const [name, setName] = useState('')
  const [mode, setMode] = useState<ScheduleMode>('Continuous')
  const [frequencySeconds, setFrequencySeconds] = useState(60)
  const [cronExpression, setCronExpression] = useState('0 * * * *')

  const create = async (e: React.FormEvent) => {
    e.preventDefault()
    const task: ReplicationTaskConfig = {
      name,
      enabled: true,
      scheduling:
        mode === 'Continuous'
          ? { mode, frequencySeconds, cronExpression: null }
          : { mode, frequencySeconds: null, cronExpression },
      changeProcessing: {
        reader: { kind: READER_KINDS[0], parallelism: 1, options: {} },
        cache: { kind: CACHE_KINDS[0], options: {} },
        writer: { kind: WRITER_KINDS[0], parallelism: 1, options: {} },
      },
    }
    await upsert.mutateAsync({ name, task })
    navigate(`/replications/${encodeURIComponent(name)}`)
  }

  return (
    <div>
      <div className="page-header">
        <h1>Replications</h1>
        {!creating && (
          <button className="btn btn-primary" onClick={() => setCreating(true)} data-testid="new-replication-button">
            New Replication
          </button>
        )}
      </div>

      <ErrorBanner error={error ?? upsert.error} />

      <div className="card">
        {isLoading && <p className="muted">Loading…</p>}
        {names && names.length === 0 && <p className="empty-state">No replications yet.</p>}
        {names && names.length > 0 && (
          <table data-testid="replications-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Status</th>
                <th>Schedule</th>
              </tr>
            </thead>
            <tbody>
              {names.map((n) => (
                <ReplicationRow key={n} name={n} />
              ))}
            </tbody>
          </table>
        )}
      </div>

      {creating && (
        <div className="card">
          <h2>New Replication</h2>
          <form onSubmit={create}>
            <div className="form-grid">
              <div className="form-field span-2">
                <label htmlFor="repl-name">Name</label>
                <input
                  id="repl-name"
                  required
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  data-testid="replication-name-input"
                />
              </div>
              <div className="form-field">
                <label htmlFor="repl-mode">Schedule</label>
                <select id="repl-mode" value={mode} onChange={(e) => setMode(e.target.value as ScheduleMode)}>
                  <option value="Continuous">Continuous</option>
                  <option value="Periodic">Periodic (cron)</option>
                </select>
              </div>
              {mode === 'Continuous' ? (
                <div className="form-field">
                  <label htmlFor="repl-freq">Frequency (seconds)</label>
                  <input
                    id="repl-freq"
                    type="number"
                    min={1}
                    value={frequencySeconds}
                    onChange={(e) => setFrequencySeconds(Number(e.target.value))}
                  />
                </div>
              ) : (
                <div className="form-field">
                  <label htmlFor="repl-cron">Cron Expression</label>
                  <input id="repl-cron" value={cronExpression} onChange={(e) => setCronExpression(e.target.value)} />
                </div>
              )}
            </div>
            <p className="muted">
              Reader/cache/writer default to Change Tracking + staging table + MERGE — adjustable from the
              replication's detail page.
            </p>
            <div className="form-actions">
              <button type="submit" className="btn btn-primary" disabled={upsert.isPending} data-testid="create-replication-button">
                {upsert.isPending ? 'Creating…' : 'Create'}
              </button>
              <button type="button" className="btn" onClick={() => setCreating(false)}>
                Cancel
              </button>
            </div>
          </form>
        </div>
      )}
    </div>
  )
}
