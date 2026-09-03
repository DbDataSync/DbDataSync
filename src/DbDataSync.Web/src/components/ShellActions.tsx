import { useContext, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { ShellActionsSlot } from './shellActionsSlot'

/**
 * Renders its children into `AppShell`'s action bar from anywhere beneath it — see phase 88.
 *
 * **A portal rather than a prop, because the things that belong up there are owned down here.** The
 * shell's `actions` prop works for a page that knows what its own chrome should say; a countdown
 * belongs to a panel three levels down a routed outlet, and threading one up would mean every
 * intermediate component carrying a prop about a child it does not otherwise know about — and the
 * `MetricsCard` in the detail rail and the `RunsPanel` behind the outlet would each need their own
 * channel.
 *
 * Several components may mount one at once; they appear in mount order, before the notification
 * bell and the signed-in identity, which stay the rightmost things on every screen.
 */
export function ShellActions({ children }: { children: ReactNode }) {
  const slot = useContext(ShellActionsSlot)
  return slot ? createPortal(children, slot) : null
}
