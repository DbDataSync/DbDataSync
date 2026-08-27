import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { AppShell } from '../components/AppShell'
import { useDeleteReplication } from '../api/hooks'
import { OverviewPanel } from './replication-detail/OverviewPanel'
import { TableMappingsPanel } from './replication-detail/TableMappingsPanel'
import { RunsPanel, type RunsCommand } from './replication-detail/RunsPanel'
import { HistoryPanel } from './replication-detail/HistoryPanel'

type Tab = 'overview' | 'mappings' | 'runs' | 'history'

/** The design labels the config-history tab "Version Control"; it is the same git log. */
const TABS: { id: Tab; label: string; testId: string }[] = [
  { id: 'overview', label: 'Overview', testId: 'tab-overview' },
  { id: 'mappings', label: 'Table Mappings', testId: 'tab-mappings' },
  { id: 'runs', label: 'Runs', testId: 'tab-runs' },
  { id: 'history', label: 'Version Control', testId: 'tab-history' },
]

export function ReplicationDetailPage() {
  const { name } = useParams<{ name: string }>()
  const navigate = useNavigate()
  const del = useDeleteReplication()
  const [tab, setTab] = useState<Tab>('overview')
  // A command from the chrome down into the Runs panel. The nonce is what makes a repeat of the
  // same command distinguishable from no command at all.
  const [command, setCommand] = useState<RunsCommand | null>(null)
  const send = (kind: RunsCommand['kind']) => {
    setTab('runs')
    setCommand({ kind, nonce: Date.now() })
  }

  if (!name) return null

  const onDelete = async () => {
    await del.mutateAsync(name)
    navigate('/replications')
  }

  return (
    <AppShell
      crumbs={[{ label: 'Replications', to: '/replications' }, { label: name, mono: true }]}
      tabs={
        <>
          {TABS.map((t) => (
            <button
              key={t.id}
              className={`tab ${tab === t.id ? 'active' : ''}`}
              onClick={() => setTab(t.id)}
              data-testid={t.testId}
            >
              {t.label}
            </button>
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
      {tab === 'overview' && <OverviewPanel replicationName={name} />}
      {tab === 'mappings' && <TableMappingsPanel replicationName={name} />}
      {tab === 'runs' && <RunsPanel replicationName={name} command={command} />}
      {tab === 'history' && <HistoryPanel replicationName={name} />}
    </AppShell>
  )
}
