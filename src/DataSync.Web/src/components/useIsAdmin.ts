import { useAuthStatus } from '../api/hooks'

/**
 * Whether the signed-in user may change anything.
 *
 * A Save button that always answers 403 is worse than no Save button — it invites somebody to do
 * work and then throws it away — so the affordances a viewer cannot use are not rendered.
 */
export function useIsAdmin() {
  const { data: status } = useAuthStatus()
  // Undefined while loading, and on an authentication-disabled deployment where the server reports
  // authenticated with no role. Treating "unknown" as admin keeps a trusted-network install usable.
  return status?.role !== 'Viewer'
}
