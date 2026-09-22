import { NavLink } from 'react-router-dom'
import { tabClass } from './tabClass'

/**
 * The Admin area's own tab strip — Configuration (phase 81), Certificate (phase 83) and (phase 159)
 * Updates, side by side. Drivers, Libraries and Files moved out into their own top-level rail section
 * (`DriversTabs`) — what someone configuring a replication reaches for, not host administration in the
 * sense these three are. A shared component rather than each page inlining its own `NavLink`s, so
 * adding a destination later means one edit, not every page staying in sync by hand.
 */
export function AdminTabs() {
  return (
    <>
      <NavLink to="/admin/config" className={tabClass} data-testid="admin-tab-config">
        Configuration
      </NavLink>
      <NavLink to="/admin/certificate" className={tabClass} data-testid="admin-tab-certificate">
        Certificate
      </NavLink>
      <NavLink to="/admin/updates" className={tabClass} data-testid="admin-tab-updates">
        Updates
      </NavLink>
    </>
  )
}
