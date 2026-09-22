import { NavLink } from 'react-router-dom'
import { tabClass } from './tabClass'

/**
 * The Admin area's own tab strip — Configuration (phase 81), Certificate (phase 83), and (phase 118)
 * Drivers and Libraries, (phase 173V) Files, and (phase 159) Updates, side by side. A shared component
 * rather than each page inlining its own `NavLink`s, so adding a destination later means one edit, not
 * every page staying in sync by hand.
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
      <NavLink to="/admin/drivers" className={tabClass} data-testid="admin-tab-drivers">
        Drivers
      </NavLink>
      <NavLink to="/admin/libraries" className={tabClass} data-testid="admin-tab-libraries">
        Libraries
      </NavLink>
      <NavLink to="/admin/files" className={tabClass} data-testid="admin-tab-files">
        Files
      </NavLink>
      <NavLink to="/admin/updates" className={tabClass} data-testid="admin-tab-updates">
        Updates
      </NavLink>
    </>
  )
}
