import { useState } from 'react'
import { ErrorBanner } from '../components/ErrorBanner'
import { useConnections, useDeleteConnection, useUpsertConnection } from '../api/hooks'
import type { AuthMode, ConnectionInput } from '../api/types'

const emptyForm: ConnectionInput = {
  name: '',
  driverType: 'MsSql',
  host: '',
  port: 1433,
  database: '',
  authMode: 'SqlAuth',
  userId: '',
  password: '',
}

export function ConnectionsPage() {
  const { data: connections, isLoading, error } = useConnections()
  const upsert = useUpsertConnection()
  const del = useDeleteConnection()
  const [editing, setEditing] = useState<ConnectionInput | null>(null)

  const startCreate = () => setEditing({ ...emptyForm })
  const startEdit = (name: string) => {
    const existing = connections?.find((c) => c.name === name)
    if (!existing) return
    setEditing({
      name: existing.name,
      driverType: existing.driverType,
      host: existing.host,
      port: existing.port,
      database: existing.database,
      authMode: existing.authMode,
      userId: existing.userId ?? '',
      password: '', // never pre-filled — leaving blank keeps the existing stored credential
    })
  }

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!editing) return
    const input: ConnectionInput = { ...editing, password: editing.password || undefined }
    await upsert.mutateAsync({ name: editing.name, input })
    setEditing(null)
  }

  return (
    <div>
      <div className="page-header">
        <h1>Connections</h1>
        {!editing && (
          <button className="btn btn-primary" onClick={startCreate} data-testid="new-connection-button">
            New Connection
          </button>
        )}
      </div>

      <ErrorBanner error={error ?? upsert.error ?? del.error} />

      <div className="card">
        {isLoading && <p className="muted">Loading…</p>}
        {connections && connections.length === 0 && <p className="empty-state">No connections yet.</p>}
        {connections && connections.length > 0 && (
          <table data-testid="connections-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Driver</th>
                <th>Host</th>
                <th>Database</th>
                <th>Auth</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {connections.map((c) => (
                <tr key={c.name}>
                  <td>{c.name}</td>
                  <td>{c.driverType}</td>
                  <td>
                    {c.host}
                    {c.port ? `:${c.port}` : ''}
                  </td>
                  <td>{c.database ?? '—'}</td>
                  <td>{c.authMode}</td>
                  <td>
                    <div className="row">
                      <button className="btn btn-sm" onClick={() => startEdit(c.name)}>
                        Edit
                      </button>
                      <button
                        className="btn btn-sm btn-danger"
                        onClick={() => del.mutate(c.name)}
                        data-testid={`delete-connection-${c.name}`}
                      >
                        Delete
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {editing && (
        <div className="card">
          <h2>{connections?.some((c) => c.name === editing.name) ? 'Edit Connection' : 'New Connection'}</h2>
          <form onSubmit={save}>
            <div className="form-grid">
              <div className="form-field">
                <label htmlFor="conn-name">Name</label>
                <input
                  id="conn-name"
                  required
                  disabled={connections?.some((c) => c.name === editing.name)}
                  value={editing.name}
                  onChange={(e) => setEditing({ ...editing, name: e.target.value })}
                  data-testid="connection-name-input"
                />
              </div>
              <div className="form-field">
                <label htmlFor="conn-host">Host</label>
                <input
                  id="conn-host"
                  required
                  value={editing.host}
                  onChange={(e) => setEditing({ ...editing, host: e.target.value })}
                  data-testid="connection-host-input"
                />
              </div>
              <div className="form-field">
                <label htmlFor="conn-port">Port</label>
                <input
                  id="conn-port"
                  type="number"
                  value={editing.port ?? ''}
                  onChange={(e) => setEditing({ ...editing, port: e.target.value ? Number(e.target.value) : null })}
                />
              </div>
              <div className="form-field">
                <label htmlFor="conn-database">Database</label>
                <input
                  id="conn-database"
                  value={editing.database ?? ''}
                  onChange={(e) => setEditing({ ...editing, database: e.target.value })}
                  data-testid="connection-database-input"
                />
              </div>
              <div className="form-field">
                <label htmlFor="conn-auth">Auth Mode</label>
                <select
                  id="conn-auth"
                  value={editing.authMode}
                  onChange={(e) => setEditing({ ...editing, authMode: e.target.value as AuthMode })}
                >
                  <option value="SqlAuth">SQL Auth</option>
                  <option value="IntegratedAuth">Integrated Auth</option>
                </select>
              </div>
              {editing.authMode === 'SqlAuth' && (
                <>
                  <div className="form-field">
                    <label htmlFor="conn-user">User ID</label>
                    <input
                      id="conn-user"
                      value={editing.userId ?? ''}
                      onChange={(e) => setEditing({ ...editing, userId: e.target.value })}
                      data-testid="connection-userid-input"
                    />
                  </div>
                  <div className="form-field">
                    <label htmlFor="conn-password">Password</label>
                    <input
                      id="conn-password"
                      type="password"
                      placeholder="Leave blank to keep existing"
                      value={editing.password ?? ''}
                      onChange={(e) => setEditing({ ...editing, password: e.target.value })}
                      data-testid="connection-password-input"
                    />
                  </div>
                </>
              )}
            </div>
            <div className="form-actions">
              <button type="submit" className="btn btn-primary" disabled={upsert.isPending} data-testid="save-connection-button">
                {upsert.isPending ? 'Saving…' : 'Save'}
              </button>
              <button type="button" className="btn" onClick={() => setEditing(null)}>
                Cancel
              </button>
            </div>
          </form>
        </div>
      )}
    </div>
  )
}
