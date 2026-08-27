import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { AppShell, SectionTabs } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { useConnections, useTestConnection } from '../api/hooks'
import type { ConnectionTestReport } from '../api/types'

const COLUMNS = '1.1fr .8fr 1.4fr 1.1fr .9fr 1.2fr'

/**
 * The mockup's `Reachable` column, populated **only on demand**.
 *
 * Deliberately not a background poll: a list that silently opens every configured database because it
 * rendered is a surprising thing for a console to do, and would turn a page load into N connection
 * attempts against production. "Test all" is one click; nothing happens without it.
 */
export function ConnectionsPage() {
  const { data: connections, isLoading, error } = useConnections()
  const navigate = useNavigate()
  const test = useTestConnection()
  const [reports, setReports] = useState<Record<string, ConnectionTestReport | 'pending'>>({})

  const testAll = async () => {
    const names = (connections ?? []).map((c) => c.name)
    setReports(Object.fromEntries(names.map((n) => [n, 'pending' as const])))
    await Promise.all(names.map(async (name) => {
      // Each result lands as it arrives rather than after the slowest — an unreachable host takes its
      // connect timeout to answer, and holding every other row behind it would look like a hang.
      const report = await test.mutateAsync(name).catch(() => null)
      setReports((prior) => report ? { ...prior, [name]: report } : omit(prior, name))
    }))
  }

  return (
    <AppShell crumbs={[{ label: 'Connections' }]} tabs={<SectionTabs />}>
      <div className="pane">
        <div className="page-head">
          <h1 className="page-title">Connections</h1>
          <span className="page-note">
            {connections ? `${connections.length} configured` : '…'}
          </span>
          <div className="right" style={{ display: 'flex', gap: 8 }}>
            {(connections?.length ?? 0) > 0 && (
              <button className="btn" onClick={testAll} data-testid="test-all-connections-button">
                Test all
              </button>
            )}
            <button className="btn btn-primary" onClick={() => navigate('/connections/new')} data-testid="new-connection-button">
              New connection
            </button>
          </div>
        </div>

        <ErrorBanner error={error} />

        <div className="card flush" data-testid="connections-table">
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
            <span>Name</span><span>Driver</span><span>Host</span><span>Database</span><span>Auth</span><span>Reachable</span>
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
              <Reachability report={reports[c.name]} testId={`reachable-${c.name}`} />
            </button>
          ))}
        </div>
      </div>
    </AppShell>
  )
}

function Reachability({ report, testId }: { report: ConnectionTestReport | 'pending' | undefined; testId: string }) {
  // Untested is its own state, distinct from unreachable. Showing a dot before anyone has asked would
  // be an invented reading, which is the reason this column was left out until now.
  if (report === undefined) return <span className="dim" data-testid={testId}>—</span>
  if (report === 'pending') return <span className="dim" data-testid={testId}>testing…</span>

  return (
    <span className="status" data-testid={testId}>
      <span className={`dot ${report.succeeded ? 'dot-ok' : 'dot-bad'}`} />
      {report.succeeded ? `reachable · ${Math.round(report.connectMs)}ms` : 'unreachable'}
    </span>
  )
}

function omit<T>(record: Record<string, T>, key: string): Record<string, T> {
  const { [key]: _dropped, ...rest } = record
  return rest
}
