import { useOutletContext } from 'react-router-dom'
import type { ReplicationOutletContext } from '../ReplicationDetailPage'
import { OverviewPanel } from './OverviewPanel'
import { TableMappingsPanel } from './TableMappingsPanel'
import { HistoryPanel } from './HistoryPanel'
import { MonitoringSection } from './MonitoringPanel'

/**
 * The four routed tabs, each a thin adapter that takes the replication name from the layout route's
 * outlet context. Keeping the panels themselves unaware of routing means they stay ordinary
 * components — testable, and reusable if a screen ever composes more than one.
 *
 * Five until phase 103, which folded Runs under Monitoring as its **Run History** sub-tab — see
 * `MonitoringSection` for that layout.
 */
export function OverviewTab() {
  const { replicationName, draft, setDraft, enabled } = useOutletContext<ReplicationOutletContext>()
  // The layout route seeds the draft from the first load; until then there is nothing to edit.
  if (!draft) return <div className="pane"><span className="hint">Loading…</span></div>
  return <OverviewPanel replicationName={replicationName} draft={draft} setDraft={setDraft} enabled={enabled} />
}

/** A layout in its own right — the mapping routes nest inside it. */
export function MappingsTab() {
  return <TableMappingsPanel replicationName={useReplicationName()} />
}

/** A layout in its own right too, since phase 103 — Current Status and Run History nest inside it. */
export function MonitoringTab() {
  const { replicationName, command } = useOutletContext<ReplicationOutletContext>()
  return <MonitoringSection replicationName={replicationName} command={command} />
}

export function HistoryTab() {
  return <HistoryPanel replicationName={useReplicationName()} />
}

function useReplicationName() {
  return useOutletContext<ReplicationOutletContext>().replicationName
}
