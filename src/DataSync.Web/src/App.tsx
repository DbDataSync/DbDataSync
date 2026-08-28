import { Navigate, Route, Routes } from 'react-router-dom'
import { ConnectionEditPage } from './pages/ConnectionEditPage'
import { ConnectionsPage } from './pages/ConnectionsPage'
import { ReplicationsPage } from './pages/ReplicationsPage'
import { ScriptEditPage } from './pages/ScriptEditPage'
import { ScriptsPage } from './pages/ScriptsPage'
import { ReplicationDetailPage } from './pages/ReplicationDetailPage'
import { HistoryTab, MappingsTab, OverviewTab, RunsTab } from './pages/replication-detail/tabs'
import { MappingEditorRoute, MappingsIndex } from './pages/replication-detail/TableMappingsPanel'
import { MappingPreview } from './pages/replication-detail/MappingPreview'

/**
 * Every destination has a URL.
 *
 * The replication detail screen's four tabs and the mapping the editor has open used to be component
 * state under one route, which meant a reload dropped you back on Overview and there was no way to
 * send someone a link to what you were looking at. They are routes now, nested under layout routes
 * that own the shared chrome — the tab bar does not re-render between tabs, and the mappings sidebar
 * does not re-render between mappings.
 *
 * The list screens each render their own `AppShell` rather than sharing a layout route: their
 * breadcrumb trails and tab strips genuinely differ, so there is no shared frame to hoist.
 */
export default function App() {
  return (
    <Routes>
      <Route path="/" element={<Navigate to="/replications" replace />} />

      <Route path="/replications" element={<ReplicationsPage />} />
      <Route path="/replications/:name" element={<ReplicationDetailPage />}>
        {/* Overview is the landing tab, so a bare /replications/:name is that rather than a blank
            frame. `replace`, so Back leaves the replication instead of bouncing off the redirect. */}
        <Route index element={<Navigate to="overview" replace />} />
        <Route path="overview" element={<OverviewTab />} />
        <Route path="mappings" element={<MappingsTab />}>
          <Route index element={<MappingsIndex />} />
          {/* `new` before the parameter for readability; React Router ranks the static segment higher
              either way. It does mean a mapping literally named "new" is unreachable — the same
              sentinel this screen has always used, and worth revisiting only if anyone hits it. */}
          <Route path="new" element={<MappingEditorRoute />} />
          <Route path=":mappingName" element={<MappingEditorRoute />} />
          {/* Beside the editor rather than inside it: the preview is about the mapping as
              *saved*, which is not what an editor with unsaved changes is showing. */}
          <Route path=":mappingName/preview" element={<MappingPreview />} />
        </Route>
        <Route path="runs" element={<RunsTab />} />
        <Route path="history" element={<HistoryTab />} />
      </Route>

      <Route path="/connections" element={<ConnectionsPage />} />
      <Route path="/connections/:name" element={<ConnectionEditPage />} />

      <Route path="/scripts" element={<ScriptsPage />} />
      <Route path="/scripts/:name" element={<ScriptEditPage />} />

      {/* A mistyped or stale URL lands somewhere real rather than on an empty frame with chrome. */}
      <Route path="*" element={<Navigate to="/replications" replace />} />
    </Routes>
  )
}
