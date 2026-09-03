import { useOutletContext } from 'react-router-dom'
import type { ReplicationOutletContext } from '../ReplicationDetailPage'
import { OverviewPanel } from './OverviewPanel'
import { TableMappingsPanel } from './TableMappingsPanel'
import { RunsPanel } from './RunsPanel'
import { HistoryPanel } from './HistoryPanel'
import { MonitoringPanel } from './MonitoringPanel'

/**
 * The five routed tabs, each a thin adapter that takes the replication name from the layout route's
 * outlet context. Keeping the panels themselves unaware of routing means they stay ordinary
 * components — testable, and reusable if a screen ever composes more than one.
 */
export function OverviewTab() {
  const { replicationName, draft, setDraft } = useOutletContext<ReplicationOutletContext>()
  // The layout route seeds the draft from the first load; until then there is nothing to edit.
  if (!draft) return <div className="pane"><span className="hint">Loading…</span></div>
  return <OverviewPanel replicationName={replicationName} draft={draft} setDraft={setDraft} />
}

/** A layout in its own right — the mapping routes nest inside it. */
export function MappingsTab() {
  return <TableMappingsPanel replicationName={useReplicationName()} />
}

export function RunsTab() {
  const { replicationName, command } = useOutletContext<ReplicationOutletContext>()
  return <RunsPanel replicationName={replicationName} command={command} />
}

export function MonitoringTab() {
  return <MonitoringPanel replicationName={useReplicationName()} />
}

export function HistoryTab() {
  return <HistoryPanel replicationName={useReplicationName()} />
}

function useReplicationName() {
  return useOutletContext<ReplicationOutletContext>().replicationName
}
