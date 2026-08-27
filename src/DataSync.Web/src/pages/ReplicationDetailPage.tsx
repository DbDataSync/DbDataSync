import { useState } from 'react'
import { NavLink, Outlet, useNavigate, useParams } from 'react-router-dom'
import { AppShell } from '../components/AppShell'
import { tabClass } from '../components/tabClass'
import { useDeleteReplication } from '../api/hooks'
import type { RunsCommand } from './replication-detail/RunsPanel'

/** The design labels the config-history tab "Version Control"; it is the same git log. */
const TABS: { path: string; label: string; testId: string }[] = [
  { path: 'overview', label: 'Overview', testId: 'tab-overview' },
  { path: 'mappings', label: 'Table Mappings', testId: 'tab-mappings' },
  { path: 'runs', label: 'Runs', testId: 'tab-runs' },
  { path: 'history', label: 'Version Control', testId: 'tab-history' },
]

/**
 * A layout route: the four tabs are four routes sharing this chrome, rendered through the `Outlet`.
 *
 * The chrome does not unmount when the tab changes, which is what lets the Backfill…/Run Now buttons
 * live up here and still reach the Runs panel — see `RunsCommand`.
 */
export function ReplicationDetailPage() {
  const { name } = useParams<{ name: string }>()
  const navigate = useNavigate()
  const del = useDeleteReplication()
  // A command from the chrome down into the Runs panel. The nonce is what makes a repeat of the
  // same command distinguishable from no command at all.
  //
  // Deliberately component state rather than location state: an action must not re-fire because
  // someone reloaded the page or pressed Back onto this history entry.
  const [command, setCommand] = useState<RunsCommand | null>(null)
  const send = (kind: RunsCommand['kind']) => {
    navigate(`/replications/${encodeURIComponent(name!)}/runs`)
    setCommand({ kind, nonce: Date.now() })
  }

  if (!name) return null

  const onDelete = async () => {
    await del.mutateAsync(name)
    navigate('/replications')
  }

  return (
    <AppShell
      crumbs={[{ label: 'Replications', to: '/replications' }, { label: name, mono: true, heading: true }]}
      tabs={
        <>
          {TABS.map((t) => (
            <NavLink
              key={t.path}
              to={`/replications/${encodeURIComponent(name)}/${t.path}`}
              className={tabClass}
              data-testid={t.testId}
            >
              {t.label}
            </NavLink>
          ))}
          <div className="actions">
            {/* The design puts the run controls in the chrome, reachable from any tab — clicking
                either moves to Runs so the result is visible where it lands. */}
            <button
              className="btn btn-chrome"
              onClick={() => send('backfill')}
              data-testid="backfill-button"
            >
              Backfill…
            </button>
            <button
              className="btn btn-primary btn-chrome"
              onClick={() => send('run')}
              data-testid="trigger-run-button"
            >
              Run Now
            </button>
            <button className="btn btn-danger btn-chrome" onClick={onDelete} data-testid="delete-replication-button">
              Delete
            </button>
          </div>
        </>
      }
    >
      {/* Each tab's panel takes the replication name and the pending command from here rather than
          re-deriving them, so a panel never has to know it is mounted under a layout route. */}
      <Outlet context={{ replicationName: name, command } satisfies ReplicationOutletContext} />
    </AppShell>
  )
}

export interface ReplicationOutletContext {
  replicationName: string
  command: RunsCommand | null
}
