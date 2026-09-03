import { NavLink } from 'react-router-dom'
import { tabClass } from './tabClass'

/**
 * The Admin area's own tab strip — Configuration (phase 81) and Certificates (phase 83), side by side
 * the way the phase 83 doc describes this screen as living "alongside phase 81's config table." A
 * shared component rather than each page inlining its own two `NavLink`s, so adding a third Admin
 * destination later means one edit, not two pages staying in sync by hand.
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
    </>
  )
}
