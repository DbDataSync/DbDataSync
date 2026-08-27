import { useNavigate } from 'react-router-dom'
import { AppShell, SectionTabs } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { useConnections } from '../api/hooks'

const COLUMNS = '1.1fr .8fr 1.4fr 1.1fr .9fr'

/**
 * The mockup carries a `Reachable` column and a warning banner about an unresponsive endpoint.
 * Nothing tests a connection, so both are omitted rather than filled with a value that would look
 * like a reading — see phase-015.
 */
export function ConnectionsPage() {
  const { data: connections, isLoading, error } = useConnections()
  const navigate = useNavigate()

  return (
    <AppShell crumbs={[{ label: 'Connections' }]} tabs={<SectionTabs active="connections" />}>
      <div className="pane">
        <div className="page-head">
          <span className="page-title">Connections</span>
          <span className="page-note">
            {connections ? `${connections.length} configured` : '…'}
          </span>
          <div className="right">
            <button className="btn btn-primary" onClick={() => navigate('/connections/new')} data-testid="new-connection-button">
              New connection
            </button>
          </div>
        </div>

        <ErrorBanner error={error} />

        <div className="card flush" data-testid="connections-table">
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
            <span>Name</span><span>Driver</span><span>Host</span><span>Database</span><span>Auth</span>
          </div>
          {isLoading && <div className="empty">Loading…</div>}
          {connections?.length === 0 && <div className="empty">No connections yet.</div>}
          {(connections ?? []).map((c) => (
            <button
              key={c.name}
              className="grid-row"
              style={{ gridTemplateColumns: COLUMNS, gap: 14 }}
              onClick={() => navigate(`/connections/${encodeURIComponent(c.name)}`)}
              data-testid={`connection-row-${c.name}`}
            >
              <span className="name">{c.name}</span>
              <span>{c.driverType}</span>
              <span className="dim">{c.host}{c.port ? `:${c.port}` : ''}</span>
              <span className="dim">{c.database ?? '—'}</span>
              <span className="dim">{c.authMode}</span>
            </button>
          ))}
        </div>
      </div>
    </AppShell>
  )
}
