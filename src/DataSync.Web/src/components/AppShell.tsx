import { useState, type ReactNode } from 'react'
import { NavLink } from 'react-router-dom'
import { ShellActionsSlot } from './shellActionsSlot'
import { NotificationBell } from './NotificationBell'
import { SignedInAs } from './SignIn'
import { useIsAdmin } from './useIsAdmin'
import { CodeIcon, DatabaseIcon, FlowIcon, GearIcon, GridIcon, LogoIcon } from './icons'

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
  // The API enforces this for real (Policies.Admin on every admin/config endpoint) — hiding the rail
  // item is only about not offering a Viewer a destination that would 403 on arrival, the same
  // instinct useIsAdmin's own doc comment states for the Save buttons it gates.
  const isAdmin = useIsAdmin()

  // State rather than a ref, deliberately: a portal needs its host element to exist before it can
  // render into it, and a ref assignment does not re-render the subscribers. Holding the node in
  // state means the first render publishes null — `ShellActions` renders nothing — and the second
  // publishes the element, which is when the countdowns appear.
  const [actionSlot, setActionSlot] = useState<HTMLElement | null>(null)

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
        {isAdmin && (
          // /admin itself, not /admin/config directly — Admin now has two sections (Configuration,
          // phase 81; Certificate, phase 83), and linking to the bare section root is what lets this
          // rail item stay lit on either one, the same "stays lit while you are anywhere inside it"
          // behaviour the comment above already describes for every other rail item.
          <NavLink
            to="/admin"
            className={({ isActive }) => `rail-item ${isActive ? 'active' : ''}`}
            title="Admin"
            data-testid="rail-admin"
          >
            <GearIcon />
          </NavLink>
        )}
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
          {/* Who is signed in, on every screen — it belongs to the shell rather than to whichever
              page remembered to render it. Absent entirely where the deployment does not
              authenticate. */}
          <div className="right">
            {actions}
            {/* What the content below wants to say up here — see `ShellActions`. Left of the bell
                and the identity, which stay the rightmost things on every screen. */}
            <span className="shell-actions" ref={setActionSlot} />
            {/* Beside the signed-in identity, on every screen and for the same reason: a
                notification is about the deployment, not about whichever page happens to be open. */}
            <NotificationBell />
            <SignedInAs />
          </div>
        </header>

        {tabs && <div className="tabbar">{tabs}</div>}

        <div className="content">
          <ShellActionsSlot.Provider value={actionSlot}>{children}</ShellActionsSlot.Provider>
        </div>
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
