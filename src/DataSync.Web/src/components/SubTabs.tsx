import { NavLink } from 'react-router-dom'

export interface SubTab {
  /** The path segment relative to the section's base. `null` is the index tab — the section's own
   * URL, with no segment of its own. */
  path: string | null
  label: string
  testId: string
  /** An absolute path, for a tab whose route already exists elsewhere in the tree (Preview SQL and
   * Verify both predate this bar and keep the URLs they had). */
  to?: string
  /** A count worth seeing without opening the tab. Zero renders nothing at all — a "0" badge is a
   * decoration that says the same thing as no badge, while costing the eye a stop. */
  badge?: number
}

/**
 * A second row of tabs, inside a panel, each one a real route.
 *
 * Routed rather than component state for the reason the top-level tabs already are: a reload should
 * land where you were, and "look at this replication's provisioning" should be a link you can send.
 * The exception is the index tab — Notes, on both screens — which is the section's own URL. That is
 * not an oversight: it means the plain, unqualified URL for a replication or a mapping is the one
 * that opens what it is *for*, and nobody has to know a path segment to link to a mapping.
 */
export function SubTabs({ base, tabs, testId }: { base: string; tabs: SubTab[]; testId?: string }) {
  return (
    <div className="subtabbar" data-testid={testId}>
      {tabs.map((tab) => (
        <NavLink
          key={tab.testId}
          to={tab.to ?? (tab.path === null ? base : `${base}/${tab.path}`)}
          // Only the index tab needs `end`; without it every other tab's path is a prefix match and
          // the index would light up on all of them.
          end={tab.path === null}
          className={({ isActive }) => `subtab ${isActive ? 'active' : ''}`}
          data-testid={tab.testId}
        >
          {tab.label}
          {!!tab.badge && (
            <span className="subtab-badge" data-testid={`${tab.testId}-badge`}>{tab.badge}</span>
          )}
        </NavLink>
      ))}
    </div>
  )
}
