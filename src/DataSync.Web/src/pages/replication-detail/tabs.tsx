import { useOutletContext } from 'react-router-dom'
import type { ReplicationOutletContext } from '../ReplicationDetailPage'
import { OverviewPanel } from './OverviewPanel'
import { TableMappingsPanel } from './TableMappingsPanel'
import { RunsPanel } from './RunsPanel'
import { HistoryPanel } from './HistoryPanel'

/**
 * The four routed tabs, each a thin adapter that takes the replication name from the layout route's
 * outlet context. Keeping the panels themselves unaware of routing means they stay ordinary
 * components — testable, and reusable if a screen ever composes more than one.
 */
export function OverviewTab() {
  return <OverviewPanel replicationName={useReplicationName()} />
}

/** A layout in its own right — the mapping routes nest inside it. */
export function MappingsTab() {
  return <TableMappingsPanel replicationName={useReplicationName()} />
}

export function RunsTab() {
  const { replicationName, command } = useOutletContext<ReplicationOutletContext>()
  return <RunsPanel replicationName={replicationName} command={command} />
}

export function HistoryTab() {
  return <HistoryPanel replicationName={useReplicationName()} />
}

function useReplicationName() {
  return useOutletContext<ReplicationOutletContext>().replicationName
}
