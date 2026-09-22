import { NavLink } from 'react-router-dom'
import { tabClass } from './tabClass'

/**
 * The Drivers section's own tab strip — Drivers, Libraries and Files, moved out of Admin into their
 * own top-level rail section (still admin-only) since they're what someone configuring a replication
 * reaches for, not host administration in the sense Configuration/Certificate/Updates are. Same shape
 * as `AdminTabs`, deliberately — one visual language for "a tabbed section under a rail item", not two.
 */
export function DriversTabs() {
  return (
    <>
      <NavLink to="/drivers" end className={tabClass} data-testid="drivers-tab-drivers">
        Drivers
      </NavLink>
      <NavLink to="/drivers/libraries" className={tabClass} data-testid="drivers-tab-libraries">
        Libraries
      </NavLink>
      <NavLink to="/drivers/files" className={tabClass} data-testid="drivers-tab-files">
        Files
      </NavLink>
    </>
  )
}
