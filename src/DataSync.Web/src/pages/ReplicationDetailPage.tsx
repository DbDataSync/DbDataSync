import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useDeleteReplication } from '../api/hooks'
import { OverviewPanel } from './replication-detail/OverviewPanel'
import { TableMappingsPanel } from './replication-detail/TableMappingsPanel'
import { RunsPanel } from './replication-detail/RunsPanel'
import { HistoryPanel } from './replication-detail/HistoryPanel'

type Tab = 'overview' | 'mappings' | 'runs' | 'history'

export function ReplicationDetailPage() {
  const { name } = useParams<{ name: string }>()
  const navigate = useNavigate()
  const del = useDeleteReplication()
  const [tab, setTab] = useState<Tab>('overview')

  if (!name) return null

  const onDelete = async () => {
    await del.mutateAsync(name)
    navigate('/replications')
  }

  return (
    <div>
      <div className="page-header">
        <div>
          <Link to="/replications" className="muted">
            ← Replications
          </Link>
          <h1>{name}</h1>
        </div>
        <button className="btn btn-danger" onClick={onDelete} data-testid="delete-replication-button">
          Delete
        </button>
      </div>

      <div className="section-tabs">
        <button className={tab === 'overview' ? 'active' : ''} onClick={() => setTab('overview')} data-testid="tab-overview">
          Overview
        </button>
        <button className={tab === 'mappings' ? 'active' : ''} onClick={() => setTab('mappings')} data-testid="tab-mappings">
          Table Mappings
        </button>
        <button className={tab === 'runs' ? 'active' : ''} onClick={() => setTab('runs')} data-testid="tab-runs">
          Runs
        </button>
        <button className={tab === 'history' ? 'active' : ''} onClick={() => setTab('history')} data-testid="tab-history">
          History
        </button>
      </div>

      {tab === 'overview' && <OverviewPanel replicationName={name} />}
      {tab === 'mappings' && <TableMappingsPanel replicationName={name} />}
      {tab === 'runs' && <RunsPanel replicationName={name} />}
      {tab === 'history' && <HistoryPanel replicationName={name} />}
    </div>
  )
}
