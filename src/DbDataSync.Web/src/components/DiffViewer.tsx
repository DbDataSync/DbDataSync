import { Suspense, lazy } from 'react'

export interface DiffViewerProps {
  /** Null for a file that did not exist before — an addition renders as an empty left pane. */
  before: string | null
  /** Null for one that does not exist after — a deletion. */
  after: string | null
  language?: 'yaml' | 'csharp' | 'sql'
  height?: number
  testId?: string
}

/**
 * Monaco's diff editor, loaded only when a screen actually shows one — the same arrangement
 * `CodeEditor` uses, and for the same reason: the replications list, which most sessions never leave,
 * should not pay for an editor it never renders.
 *
 * Separate from `CodeEditor` rather than a mode of it because the two wrap different Monaco objects
 * (`IStandaloneDiffEditor` owns two models and a different disposal contract), and folding them
 * together would mean a component whose props half apply depending on a flag.
 */
const MonacoDiff = lazy(() => import('./code/MonacoDiff'))

export function DiffViewer(props: DiffViewerProps) {
  return (
    <Suspense fallback={<div className="code-editor loading" style={{ height: props.height ?? 420 }} />}>
      <MonacoDiff {...props} />
    </Suspense>
  )
}
