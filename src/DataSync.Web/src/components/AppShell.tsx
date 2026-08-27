import type { ReactNode } from 'react'
import { NavLink, useLocation, useNavigate } from 'react-router-dom'
import { DatabaseIcon, FlowIcon, GridIcon, LogoIcon } from './icons'

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
  const navigate = useNavigate()
  const { pathname } = useLocation()
  const section = pathname.startsWith('/connections') ? 'connections' : 'replications'

  return (
    <>
      <nav className="rail">
        <div className="rail-mark"><LogoIcon /></div>
        <button
          className={`rail-item ${section === 'replications' ? 'active' : ''}`}
          onClick={() => navigate('/replications')}
          title="Replications"
          data-testid="rail-replications"
        >
          <FlowIcon />
        </button>
        <button
          className={`rail-item ${section === 'connections' ? 'active' : ''}`}
          onClick={() => navigate('/connections')}
          title="Connections"
          data-testid="rail-connections"
        >
          <DatabaseIcon />
        </button>
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
}

/** The two top-level sections, shown on the list screens. */
export function SectionTabs({ active }: { active: 'replications' | 'connections' }) {
  const navigate = useNavigate()
  return (
    <>
      <button
        className={`tab ${active === 'replications' ? 'active' : ''}`}
        onClick={() => navigate('/replications')}
        data-testid="tab-replications"
      >
        Replications
      </button>
      <button
        className={`tab ${active === 'connections' ? 'active' : ''}`}
        onClick={() => navigate('/connections')}
        data-testid="tab-connections"
      >
        Connections
      </button>
    </>
  )
}
