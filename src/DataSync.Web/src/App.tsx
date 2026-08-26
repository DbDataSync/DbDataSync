import { Navigate, Route, Routes } from 'react-router-dom'
import { Layout } from './components/Layout'
import { ConnectionsPage } from './pages/ConnectionsPage'
import { ReplicationsPage } from './pages/ReplicationsPage'
import { ReplicationDetailPage } from './pages/ReplicationDetailPage'

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route path="/" element={<Navigate to="/replications" replace />} />
        <Route path="/connections" element={<ConnectionsPage />} />
        <Route path="/replications" element={<ReplicationsPage />} />
        <Route path="/replications/:name" element={<ReplicationDetailPage />} />
      </Route>
    </Routes>
  )
}
