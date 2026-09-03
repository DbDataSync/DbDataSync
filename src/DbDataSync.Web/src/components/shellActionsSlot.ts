import { createContext } from 'react'

/**
 * The element in `AppShell`'s header that nested content renders into — set by the shell, read by
 * `ShellActions`.
 *
 * In a module of its own so both of those files export only components, which is what keeps fast
 * refresh working on the two of them.
 *
 * Null anywhere outside a shell, which is what makes `ShellActions` a no-op rather than a crash in
 * a component mounted on its own.
 */
export const ShellActionsSlot = createContext<HTMLElement | null>(null)
