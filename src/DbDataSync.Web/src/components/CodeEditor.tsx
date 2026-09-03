import { Suspense, lazy } from 'react'
import type { ScriptDiagnostic } from '../api/types'

export interface CodeEditorProps {
  value: string
  language: 'csharp' | 'sql'
  onChange: (value: string) => void
  /** Server-side compile or validation errors, shown as markers on the offending token. */
  diagnostics?: ScriptDiagnostic[]
  readOnly?: boolean
  minLines?: number
  maxLines?: number
  testId?: string
}

/**
 * Monaco, loaded only when a screen actually shows an editor.
 *
 * The whole editor — API, theme, worker and the two language tokenizers — is behind this dynamic
 * import, so the replications list (which most sessions never leave) does not pay for it. The fallback
 * reserves the same height, so the card does not jump when the chunk lands.
 */
const MonacoEditor = lazy(() => import('./code/MonacoEditor'))

export function CodeEditor(props: CodeEditorProps) {
  const lines = Math.max(props.minLines ?? 3, props.value.split('\n').length + 1)
  return (
    <Suspense fallback={<div className="code-editor loading" style={{ height: lines * 19 + 16 }} />}>
      <MonacoEditor {...props} />
    </Suspense>
  )
}
