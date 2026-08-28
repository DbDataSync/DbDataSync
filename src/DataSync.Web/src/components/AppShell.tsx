import type { ReactNode } from 'react'
import { NavLink } from 'react-router-dom'
import { CodeIcon, DatabaseIcon, FlowIcon, GridIcon, LogoIcon } from './icons'

/**
 * The chrome every screen sits in: a 46px icon rail, a 42px breadcrumb bar and a 46px tab bar.
 *
 * The mockups also show an environment pill, an Activity tab and a health roll-up in this bar. None
 * of those have anything behind them, and a console that displays an invented reading is worse than
 * one that displays none — see phase-015's "what the design shows that the system cannot back".
 */
export function AppShell({ crumbs, tabs, actions, children }: {
  crumbs: Crumb[]
  tabs?: ReactNode
  actions?: ReactNode
  children: ReactNode
}) {
  return (
    <>
      <nav className="rail">
        <div className="rail-mark"><LogoIcon /></div>
        {/* Not `end`: a section stays lit while you are anywhere inside it, which is what the rail is
            for. That the router decides it — rather than each screen declaring which section it
            belongs to — is the point of every destination having a route. */}
        <NavLink
          to="/replications"
          className={({ isActive }) => `rail-item ${isActive ? 'active' : ''}`}
          title="Replications"
          data-testid="rail-replications"
        >
          <FlowIcon />
        </NavLink>
        <NavLink
          to="/connections"
          className={({ isActive }) => `rail-item ${isActive ? 'active' : ''}`}
          title="Connections"
          data-testid="rail-connections"
        >
          <DatabaseIcon />
        </NavLink>
        <NavLink
          to="/scripts"
          className={({ isActive }) => `rail-item ${isActive ? 'active' : ''}`}
          title="Scripts"
          data-testid="rail-scripts"
        >
          <CodeIcon />
        </NavLink>
        <span className="rail-item" style={{ color: 'var(--ink-faint)', cursor: 'default' }} title="Overview">
          <GridIcon />
        </span>
      </nav>

      <div className="app">
        <header className="topbar">
          <span className="brand">DataSync</span>
          {crumbs.map((crumb, i) => (
            <span key={`${crumb.label}-${i}`} style={{ display: 'contents' }}>
              <span className="sep">/</span>
              {crumb.to ? (
                <NavLink to={crumb.to} className={`crumb ${crumb.mono ? 'mono' : ''}`}>{crumb.label}</NavLink>
              ) : crumb.heading ? (
                <h1 className={`crumb here ${crumb.mono ? 'mono' : ''}`}>{crumb.label}</h1>
              ) : (
                <span className={`crumb here ${crumb.mono ? 'mono' : ''}`}>{crumb.label}</span>
              )}
            </span>
          ))}
          {actions && <div className="right">{actions}</div>}
        </header>

        {tabs && <div className="tabbar">{tabs}</div>}

        <div className="content">{children}</div>
      </div>
    </>
  )
}

export interface Crumb {
  label: string
  /** Omit for the current location — it renders as the leaf rather than a link. */
  to?: string
  /** Identifiers are set in the monospace face throughout the design. */
  mono?: boolean
  /**
   * Render this crumb as the page's `<h1>`. Set it only where the pane has no title of its own — the
   * replication detail screens, whose subject is named in the breadcrumb and nowhere else. A list
   * screen already has a heading in its pane, and two `<h1>`s reading the same word is worse than
   * none for anyone navigating by headings.
   */
  heading?: boolean
}
