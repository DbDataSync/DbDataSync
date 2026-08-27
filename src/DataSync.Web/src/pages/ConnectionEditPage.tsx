import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { AppShell, SectionTabs } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { Field } from '../components/Field'
import { KeyValueTable } from '../components/KeyValueTable'
import { useCapabilities, useConnections, useDeleteConnection, useTestConnection, useUpsertConnection } from '../api/hooks'
import { ConnectionTestCard } from './connection-edit/ConnectionTestCard'
import type { AuthMode, ConnectionInput, DriverType } from '../api/types'

/** Each engine's default listening port, so switching the driver does not leave the other's behind. */
const DEFAULT_PORTS: Record<DriverType, number> = { MsSql: 1433, Postgres: 5432 }

const empty: ConnectionInput = {
  name: '', driverType: 'MsSql', host: '', port: 1433, database: '',
  authMode: 'SqlAuth', userId: '', password: '', properties: {},
}

/**
 * The design gives connection editing its own screen rather than a form appended to the list, and
 * surfaces the `Properties` dictionary — which has never had a UI — as a Setting/Value table.
 *
 * The "Last test" card and "Test connection" action are here now that a driver can prove a connection
 * reaches its engine — both hidden entirely when the driver does not advertise the capability, since
 * an action that could never work is worse than no action.
 *
 * Still omitted for want of data: the environment field, and "Used by N replications" (computable only
 * by scanning every mapping of every replication).
 */
export function ConnectionEditPage() {
  const { name } = useParams<{ name: string }>()
  const isNew = !name || name === 'new'
  const navigate = useNavigate()

  const { data: connections } = useConnections()
  const upsert = useUpsertConnection()
  const del = useDeleteConnection()
  const test = useTestConnection()
  // A saved connection only: testing an unsaved draft would test whatever is on disk under that name,
  // or nothing at all.
  const capabilities = useCapabilities(isNew ? undefined : name)
  const canTest = !isNew && capabilities.data?.supportsConnectionTest === true
  const [draft, setDraft] = useState<ConnectionInput | null>(isNew ? { ...empty } : null)

  const existing = connections?.find((c) => c.name === name)

  useEffect(() => {
    if (isNew || draft || !existing) return
    setDraft({
      name: existing.name,
      driverType: existing.driverType,
      host: existing.host,
      port: existing.port,
      database: existing.database,
      authMode: existing.authMode,
      userId: existing.userId ?? '',
      password: '', // never pre-filled — blank keeps the stored credential
      properties: { ...existing.properties },
    })
  }, [isNew, draft, existing])

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!draft) return
    await upsert.mutateAsync({ name: draft.name, input: { ...draft, password: draft.password || undefined } })
    navigate('/connections')
  }

  const crumbs = [
    { label: 'Connections', to: '/connections' },
    { label: isNew ? 'new' : name!, mono: true },
  ]

  if (!draft) {
    return (
      <AppShell crumbs={crumbs} tabs={<SectionTabs />}>
        <div className="pane"><span className="hint">Loading…</span></div>
      </AppShell>
    )
  }

  return (
    <AppShell
      crumbs={crumbs}
      tabs={
        <>
          <SectionTabs />
          <div className="actions">
            {canTest && (
              <button
                className="btn btn-chrome"
                onClick={() => test.mutate(name!)}
                disabled={test.isPending}
                data-testid="test-connection-button"
              >
                {test.isPending ? 'Testing…' : 'Test connection'}
              </button>
            )}
            <button className="btn btn-chrome" onClick={() => navigate('/connections')}>Cancel</button>
            <button
              className="btn btn-primary btn-chrome"
              type="submit"
              form="connection-form"
              disabled={upsert.isPending}
              data-testid="save-connection-button"
            >
              {upsert.isPending ? 'Saving…' : 'Save'}
            </button>
            {!isNew && (
              <button
                className="btn btn-danger btn-chrome"
                onClick={async () => { await del.mutateAsync(name!); navigate('/connections') }}
                data-testid={`delete-connection-${name}`}
              >
                Delete
              </button>
            )}
          </div>
        </>
      }
    >
      <div className="pane">
        <div className="page-head">
          <h1 className="page-title mono">{isNew ? 'New connection' : draft.name}</h1>
          <span className="badge">{draft.driverType.toUpperCase()}</span>
        </div>

        <ErrorBanner error={upsert.error ?? del.error} />

        <form id="connection-form" onSubmit={save} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div className="form-grid">
            <div className="card">
              <div className="card-head"><span className="card-title">Connection</span></div>
              <div className="card-body">
                <Field label="Name">
                  <input
                    className="input"
                    required
                    disabled={!isNew}
                    value={draft.name}
                    onChange={(e) => setDraft({ ...draft, name: e.target.value })}
                    data-testid="connection-name-input"
                  />
                </Field>
                <div className="form-row">
                  <Field label="Host">
                    <input
                      className="input" required value={draft.host}
                      onChange={(e) => setDraft({ ...draft, host: e.target.value })}
                      data-testid="connection-host-input"
                    />
                  </Field>
                  <Field label="Port" alignLabel="right" style={{ width: 92, flex: 'none' }}>
                    <input
                      id="conn-port"
                      className="input"
                      type="number"
                      value={draft.port ?? ''}
                      onChange={(e) => setDraft({ ...draft, port: e.target.value ? Number(e.target.value) : null })}
                    />
                  </Field>
                </div>
                <Field label="Driver">
                  {/* Fixed after creation: the driver decides how every existing mapping's SQL is
                      built, so changing it under a live replication would silently repoint it at an
                      engine that cannot answer the same questions. */}
                  <select
                    className="select"
                    value={draft.driverType}
                    disabled={!isNew}
                    onChange={(e) => setDraft({
                      ...draft,
                      driverType: e.target.value as DriverType,
                      port: DEFAULT_PORTS[e.target.value as DriverType],
                    })}
                    data-testid="connection-driver-select"
                  >
                    <option value="MsSql">MsSql</option>
                    <option value="Postgres">Postgres</option>
                  </select>
                </Field>
                <Field label="Database">
                  <input
                    className="input"
                    value={draft.database ?? ''}
                    onChange={(e) => setDraft({ ...draft, database: e.target.value })}
                    data-testid="connection-database-input"
                  />
                </Field>
              </div>
            </div>

            <div className="card">
              <div className="card-head"><span className="card-title">Authentication</span></div>
              <div className="card-body">
                <Field label="Auth mode">
                  <select
                    className="select"
                    value={draft.authMode}
                    onChange={(e) => setDraft({ ...draft, authMode: e.target.value as AuthMode })}
                  >
                    <option value="SqlAuth">SQL Auth</option>
                    <option value="IntegratedAuth">Integrated Auth</option>
                  </select>
                </Field>
                {draft.authMode === 'SqlAuth' && (
                  <>
                    <Field label="User ID">
                      <input
                        className="input"
                        value={draft.userId ?? ''}
                        onChange={(e) => setDraft({ ...draft, userId: e.target.value })}
                        data-testid="connection-userid-input"
                      />
                    </Field>
                    <Field label="Password">
                      <input
                        className="input"
                        type="password"
                        placeholder="Leave blank to keep existing"
                        value={draft.password ?? ''}
                        onChange={(e) => setDraft({ ...draft, password: e.target.value })}
                        data-testid="connection-password-input"
                      />
                    </Field>
                  </>
                )}
              </div>
            </div>

            {canTest && (
              <ConnectionTestCard
                connectionName={name}
                report={test.data}
                pending={test.isPending}
                error={test.error}
              />
            )}
          </div>

          <div className="card">
            <div className="card-head">
              <span className="card-title">Custom properties</span>
              <span className="card-note">appended to the connection string</span>
            </div>
            <div className="card-body">
              <KeyValueTable
                value={draft.properties ?? {}}
                onChange={(properties) => setDraft({ ...draft, properties })}
                addLabel="Property name"
                testId="connection-properties"
              />
            </div>
          </div>
        </form>
      </div>
    </AppShell>
  )
}
