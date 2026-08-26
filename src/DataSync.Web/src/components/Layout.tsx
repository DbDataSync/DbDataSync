import { NavLink, Outlet } from 'react-router-dom'

export function Layout() {
  return (
    <>
      <header className="app-header">
        <span className="brand">DataSync</span>
        <nav className="app-nav">
          <NavLink to="/replications" className={({ isActive }) => (isActive ? 'active' : '')}>
            Replications
          </NavLink>
          <NavLink to="/connections" className={({ isActive }) => (isActive ? 'active' : '')}>
            Connections
          </NavLink>
        </nav>
      </header>
      <main className="app-main">
        <Outlet />
      </main>
    </>
  )
}
