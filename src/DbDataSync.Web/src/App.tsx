import { Navigate, Route, Routes } from 'react-router-dom'
import { useAuthStatus } from './api/hooks'
import { SignInScreen } from './components/SignIn'
import { InvitePage } from './pages/InvitePage'
import { ConnectionEditPage } from './pages/ConnectionEditPage'
import { ConnectionsPage } from './pages/ConnectionsPage'
import { ReplicationsPage } from './pages/ReplicationsPage'
import { ScriptEditPage } from './pages/ScriptEditPage'
import { ScriptsPage } from './pages/ScriptsPage'
import { AdminCertificatePage } from './pages/AdminCertificatePage'
import { AdminConfigPage } from './pages/AdminConfigPage'
import { AdminDriversPage } from './pages/AdminDriversPage'
import { AdminLibrariesPage } from './pages/AdminLibrariesPage'
import { ReplicationDetailPage } from './pages/ReplicationDetailPage'
import { HistoryTab, MappingsTab, MonitoringTab, OverviewTab } from './pages/replication-detail/tabs'
import {
  CustomTransformsTab, OverviewNotesTab, PipelineTab, SegmentingStrategiesTab, TargetProvisioningTab,
} from './pages/replication-detail/OverviewPanel'
import { MonitoringCurrentStatusTab, MonitoringRunHistoryTab } from './pages/replication-detail/MonitoringPanel'
import { MappingEditorRoute, MappingsIndex } from './pages/replication-detail/TableMappingsPanel'
import {
  ColumnMappingTab, MappingDiagnosticsTab, MappingNotesTab, MappingPipelineTab,
  MappingProvisioningTab, MappingSegmentingTab, MappingTransformsTab,
} from './pages/replication-detail/TableMappingForm'
import { MappingsOverview } from './pages/replication-detail/MappingsOverview'
import { MappingPreview } from './pages/replication-detail/MappingPreview'
import { VerificationPanel } from './pages/replication-detail/VerificationPanel'
import { VerificationResultPage } from './pages/replication-detail/VerificationResultPage'

/**
 * The mapping editor's tab routes, shared by `new` and `:mappingName` — the same tabs either way,
 * with the two that need a saved mapping saying so rather than being absent from one of the trees.
 *
 * A fragment rather than a component: these have to be children of a `<Route>`, and React Router
 * reads that tree structurally.
 */
const MAPPING_EDITOR_TABS = (
  <>
    <Route index element={<MappingNotesTab />} />
    <Route path="columns" element={<ColumnMappingTab />} />
    <Route path="transforms" element={<MappingTransformsTab />} />
    <Route path="segmenting" element={<MappingSegmentingTab />} />
    <Route path="provisioning" element={<MappingProvisioningTab />} />
    <Route path="pipeline" element={<MappingPipelineTab />} />
    <Route path="diagnostics" element={<MappingDiagnosticsTab />} />
  </>
)

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
  const { data: status, isLoading } = useAuthStatus()

  // Nothing is rendered until it is known whether anybody is signed in. Rendering the app first and
  // correcting to a sign-in screen would flash a shell full of failed requests.
  if (isLoading) return null

  // Redemption is reachable without a session — that is the entire point of an invitation, and
  // routing it behind the sign-in screen would be a door locked from the inside.
  if (window.location.pathname === '/invite') return <InvitePage />

  if (status && !status.authenticated) return <SignInScreen />

  return (
    <Routes>
      <Route path="/" element={<Navigate to="/replications" replace />} />

      <Route path="/replications" element={<ReplicationsPage />} />
      <Route path="/replications/:name" element={<ReplicationDetailPage />}>
        {/* Overview is the landing tab, so a bare /replications/:name is that rather than a blank
            frame. `replace`, so Back leaves the replication instead of bouncing off the redirect. */}
        <Route index element={<Navigate to="overview" replace />} />
        {/* Overview's own tabs. Notes is the index — no segment — so /overview lands on what the
            replication is *for* rather than on a setting somebody has to pick. */}
        <Route path="overview" element={<OverviewTab />}>
          <Route index element={<OverviewNotesTab />} />
          <Route path="pipeline" element={<PipelineTab />} />
          <Route path="provisioning" element={<TargetProvisioningTab />} />
          <Route path="transforms" element={<CustomTransformsTab />} />
          <Route path="segmenting" element={<SegmentingStrategiesTab />} />
        </Route>
        <Route path="mappings" element={<MappingsTab />}>
          <Route index element={<MappingsIndex />} />
          {/* The section's landing page for "which of these tables are mapped", beside the
              per-mapping editor rather than instead of it. */}
          <Route path="overview" element={<MappingsOverview />} />
          {/* `new` before the parameter for readability; React Router ranks the static segment higher
              either way. It does mean a mapping literally named "new" is unreachable — the same
              sentinel this screen has always used, and worth revisiting only if anyone hits it.

              The editor is itself a layout: its tabs are routes, and it holds the draft above them
              so switching tabs does not remount what somebody is half-way through typing. Notes is
              the index, so a link to a mapping is the mapping's own URL. */}
          <Route path="new" element={<MappingEditorRoute />}>
            {MAPPING_EDITOR_TABS}
          </Route>
          <Route path=":mappingName" element={<MappingEditorRoute />}>
            {MAPPING_EDITOR_TABS}
          </Route>
          {/* Beside the editor rather than inside it: the preview is about the mapping as
              *saved*, which is not what an editor with unsaved changes is showing. */}
          <Route path=":mappingName/preview" element={<MappingPreview />} />
          {/* Beside the editor for the same reason the preview is: a result is about the mapping as
              saved and as it stands in the two databases, not as an editor has it. */}
          <Route path=":mappingName/verification" element={<VerificationPanel />} />
          {/* A result gets its own screen. Rendered inline it was every group at once — millions of
              rows on a large check — which locked the tab up on a screen whose job is managing
              checks, not reading one. */}
          <Route path=":mappingName/verification/:resultId" element={<VerificationResultPage />} />
        </Route>
        {/* Runs lived here as its own tab until phase 103, which folded it under Monitoring as
            **Run History**. `/runs` is a real bookmark somebody may still have — phase 21 made
            routes for every screen a deliberate feature — so it redirects rather than 404ing.
            `replace`, so it does not leave an extra entry of its own in the browser's history. */}
        <Route path="runs" element={<Navigate to="../monitoring/history" replace />} />
        {/* How far behind each mapping is — see phase 86 — plus its run history, as of phase 103.
            Its own tab rather than a rail card: it is a row per mapping, and the rail is where the
            replication-wide cards live. Current Status is the index, so `/monitoring` opens the
            lag table rather than requiring a segment; Run History is what Runs used to be on its
            own. */}
        <Route path="monitoring" element={<MonitoringTab />}>
          <Route index element={<MonitoringCurrentStatusTab />} />
          <Route path="history" element={<MonitoringRunHistoryTab />} />
        </Route>
        <Route path="history" element={<HistoryTab />} />
      </Route>

      <Route path="/connections" element={<ConnectionsPage />} />
      <Route path="/connections/:name" element={<ConnectionEditPage />} />

      <Route path="/scripts" element={<ScriptsPage />} />
      <Route path="/scripts/:name" element={<ScriptEditPage />} />

      {/* The Admin area's own landing tab is Configuration — the rail's Admin icon points at this bare
          /admin path, not directly at /admin/config, so it stays lit while a certificate-screen tab is
          open too (NavLink matches by path prefix, and /admin/certificate is not a prefix match for
          /admin/config). */}
      <Route path="/admin" element={<Navigate to="/admin/config" replace />} />
      <Route path="/admin/config" element={<AdminConfigPage />} />
      <Route path="/admin/certificate" element={<AdminCertificatePage />} />
      <Route path="/admin/drivers" element={<AdminDriversPage />} />
      <Route path="/admin/libraries" element={<AdminLibrariesPage />} />

      {/* A mistyped or stale URL lands somewhere real rather than on an empty frame with chrome. */}
      <Route path="*" element={<Navigate to="/replications" replace />} />
    </Routes>
  )
}
