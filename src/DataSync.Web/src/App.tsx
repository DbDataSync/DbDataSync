import { Navigate, Route, Routes } from 'react-router-dom'
import { ConnectionEditPage } from './pages/ConnectionEditPage'
import { ConnectionsPage } from './pages/ConnectionsPage'
import { ReplicationsPage } from './pages/ReplicationsPage'
import { ReplicationDetailPage } from './pages/ReplicationDetailPage'

/**
 * No layout route any more: the design's chrome (rail, breadcrumb, tabs) varies per screen — the
 * breadcrumb trail and the tab strip are different on a list than on a detail — so each screen
 * renders its own `AppShell` rather than inheriting one shared frame.
 */
export default function App() {
  return (
    <Routes>
      <Route path="/" element={<Navigate to="/replications" replace />} />
      <Route path="/replications" element={<ReplicationsPage />} />
      <Route path="/replications/:name" element={<ReplicationDetailPage />} />
      <Route path="/connections" element={<ConnectionsPage />} />
      <Route path="/connections/:name" element={<ConnectionEditPage />} />
    </Routes>
  )
}
