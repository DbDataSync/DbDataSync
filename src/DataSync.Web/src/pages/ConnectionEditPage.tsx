import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { AppShell } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { Field } from '../components/Field'
import { ParameterForm } from '../components/ParameterForm'
import { useCapabilities, useConnectionParameters, useConnections, useDeleteConnection, useTestConnection, useUpsertConnection } from '../api/hooks'
import { ConnectionTestCard } from './connection-edit/ConnectionTestCard'
import { ScriptBindingsCard } from '../components/ScriptBindings'
import type { AuthMode, ConnectionInput, DriverType, ParameterDescriptor } from '../api/types'

/**
 * A connection as the flat values bag a `ParameterForm` reads, and back again.
 *
 * The persisted shape is unchanged — `ConnectionInput`'s own top-level fields plus a flat properties
 * dictionary — because every connection already on disk has one and a new shape would be a migration.
 * This is purely about what the form is handed.
 *
 * An empty field is **left out** rather than sent as `''`, so the driver's declared default applies:
 * that is how a new connection's Port arrives pre-filled with 1433 or 5432 without this file holding
 * a table of ports that a third driver would make stale.
 */
const PROPERTIES = 'properties'

function toValues(draft: ConnectionInput): Record<string, string> {
  const values: Record<string, string> = {
    addressMode: draft.connectionString === null || draft.connectionString === undefined ? 'host' : 'connectionString',
    authMode: draft.authMode,
    ...Object.fromEntries(
      Object.entries(draft.properties ?? {}).map(([k, v]) => [`${PROPERTIES}.${k}`, v])),
  }

  const set = (name: string, value: string | number | null | undefined) => {
    if (value !== null && value !== undefined && value !== '') values[name] = String(value)
  }

  set('host', draft.host)
  set('port', draft.port)
  set('connectionString', draft.connectionString)
  set('database', draft.database)
  set('userId', draft.userId)
  set('connectTimeoutSeconds', draft.connectTimeoutSeconds)
  set('commandTimeoutSeconds', draft.commandTimeoutSeconds)

  // The password is deliberately absent. These values go to the server to ask what a connection
  // takes, and only the two `recalc` settings change that answer — a bag carrying a plaintext
  // credential is a bag that ends up in a log line eventually. The form is handed it separately.
  return values
}

function fromValues(
  draft: ConnectionInput, values: Record<string, string>, parameters: ParameterDescriptor[],
): ConnectionInput {
  // The same fallback the form itself displays, so a field left untouched saves as what it showed.
  // Without this, a new connection whose Port reads 1433 would save with no port at all.
  const shown = (name: string) =>
    values[name] ?? parameters.find((p) => p.name === name)?.default ?? ''

  const byHost = shown('addressMode') !== 'connectionString'

  // Blank stays null rather than becoming 0 — the two mean opposite things here. Null is "use the
  // default", 0 is "no limit at all", and `Number('')` is 0, which would quietly turn every
  // untouched timeout field into an unlimited one.
  const seconds = (name: string) => (shown(name) === '' ? null : Number(shown(name)))

  return {
    ...draft,
    // One mode or the other, never both — the server rejects a connection that sets a host and a
    // connection string, because nothing decides which one is used. Clearing the other side here is
    // what makes switching the dropdown mean that.
    host: byHost ? shown('host') : null,
    port: byHost ? (shown('port') ? Number(shown('port')) : null) : null,
    connectionString: byHost ? null : shown('connectionString'),
    database: shown('database'),
    authMode: shown('authMode') as AuthMode,
    userId: shown('userId'),
    connectTimeoutSeconds: seconds('connectTimeoutSeconds'),
    commandTimeoutSeconds: seconds('commandTimeoutSeconds'),
    password: values.password ?? '',
    properties: Object.fromEntries(
      Object.entries(values)
        .filter(([k]) => k.startsWith(`${PROPERTIES}.`))
        .map(([k, v]) => [k.slice(PROPERTIES.length + 1), v])),
  }
}

// `connectionString: null` rather than absent: null is what "this connection uses host mode" looks
// like, and the address dropdown reads exactly that. `port: null` rather than a number, so the
// driver's declared default is what fills it in.
const empty: ConnectionInput = {
  name: '', driverType: 'MsSql', host: '', port: null, connectionString: null, database: '',
  authMode: 'SqlAuth', userId: '', password: '',
  // Null rather than 30/1800: a new connection has not chosen a timeout, and writing today's default
  // into it would freeze it there if the default ever moves.
  connectTimeoutSeconds: null, commandTimeoutSeconds: null, properties: {},
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

  const values = draft ? toValues(draft) : {}

  /**
   * The values the currently-displayed answer was computed from.
   *
   * Kept separately from the draft so the question is only re-asked when it has a different answer: a
   * parameter the server marked `recalc` changing. Updated from the change event rather than from an
   * effect watching the draft, which would fire on every keystroke and then have to work out whether
   * it mattered.
   */
  const [askedWith, setAskedWith] = useState<Record<string, string>>({})

  // What this connection takes, asked of the driver with the values it has so far — which is what
  // decides whether Host or Connection string is a setting at all.
  const { data: declaredParameters = [] } = useConnectionParameters(draft?.driverType, values, askedWith)

  const existing = connections?.find((c) => c.name === name)

  /** Applies what the form reports, and re-asks the driver only if a `recalc` setting moved. */
  const applyValues = (next: Record<string, string>) => {
    if (!draft) return
    const updated = fromValues(draft, next, declaredParameters)
    setDraft(updated)

    const moved = declaredParameters.some(
      (p) => p.recalc && (next[p.name] ?? p.default ?? '') !== (askedWith[p.name] ?? p.default ?? ''))
    if (moved) setAskedWith(toValues(updated))
  }

  useEffect(() => {
    if (isNew || draft || !existing) return
    const seeded: ConnectionInput = {
      name: existing.name,
      driverType: existing.driverType,
      host: existing.host,
      port: existing.port,
      connectionString: existing.connectionString,
      database: existing.database,
      authMode: existing.authMode,
      userId: existing.userId ?? '',
      connectTimeoutSeconds: existing.connectTimeoutSeconds,
      commandTimeoutSeconds: existing.commandTimeoutSeconds,
      // Never pre-filled, because the server never sends one back — blank keeps the stored credential.
      // That is the Secret parameter type's contract now, not this screen's special case.
      password: '',
      properties: { ...existing.properties },
      scripts: structuredClone(existing.scripts ?? {}),
    }
    setDraft(seeded)
    // Seeded together, so the first answer is computed from this connection's real values and a
    // connection-string connection never flashes a Host field it does not have.
    setAskedWith(toValues(seeded))
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
      <AppShell crumbs={crumbs}>
        <div className="pane"><span className="hint">Loading…</span></div>
      </AppShell>
    )
  }

  return (
    <AppShell
      crumbs={crumbs}
      tabs={
        <>
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
              <div className="card-head">
                <span className="card-title">Connection</span>
                <span className="card-note">declared by the {draft.driverType} driver</span>
              </div>
              <div className="card-body">
                {/* Name and Driver are not settings and never come from the driver. A connection's
                    name is its identity, and its driver is the selector that decides which set of
                    settings applies — it cannot be declared by the thing it selects. Both are also
                    the two fields that are fixed after creation, which nothing declared is. */}
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
                <Field label="Driver">
                  {/* Fixed after creation: the driver decides how every existing mapping's SQL is
                      built, so changing it under a live replication would silently repoint it at an
                      engine that cannot answer the same questions. */}
                  <select
                    className="select"
                    value={draft.driverType}
                    disabled={!isNew}
                    onChange={(e) => {
                      // Port is cleared rather than converted: the new driver declares its own
                      // default, and carrying 1433 over to Postgres would be this screen guessing.
                      const updated = { ...draft, driverType: e.target.value as DriverType, port: null }
                      setDraft(updated)
                      setAskedWith(toValues(updated))
                    }}
                    data-testid="connection-driver-select"
                  >
                    <option value="MsSql">MsSql</option>
                    <option value="Postgres">Postgres</option>
                    <option value="DuckDb">DuckDb</option>
                  </select>
                </Field>

                {/* Everything else — addressing, database, authentication, and the driver's own
                    settings — is declared, laid out and made conditional by the driver. This screen
                    holds no rule about what depends on what. */}
                <ParameterForm
                  parameters={declaredParameters}
                  values={{ ...values, password: draft.password ?? '' }}
                  onChange={applyValues}
                  testIdPrefix="connection-parameters"
                />
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

            {!isNew && (
              <ScriptBindingsCard
                bindings={draft.scripts ?? {}}
                inherited={{}}
                level="connection"
                onChange={(scripts) => setDraft({ ...draft, scripts })}
              />
            )}
        </form>
      </div>
    </AppShell>
  )
}
