import { useEffect, useRef } from 'react'
import { DBDATASYNC_THEME, monaco } from './monacoSetup'
import type { DiffViewerProps } from '../DiffViewer'

/**
 * Monaco's diff editor over two documents — phase 35's "View changes".
 *
 * Two documents rather than a unified patch, which is what the diff editor wants and is also the
 * better answer: a patch shows changed hunks and elides everything else, and for a config file the
 * elided part is most of the context somebody opened it to read.
 *
 * Only ever reached through `DiffViewer`, which lazy-loads it — importing this module pulls the
 * editor, so nothing that does not show one should touch it.
 */
export default function MonacoDiff({ before, after, language = 'yaml', height = 420, testId }: DiffViewerProps) {
  const host = useRef<HTMLDivElement>(null)
  const editor = useRef<monaco.editor.IStandaloneDiffEditor>(null)

  useEffect(() => {
    if (!host.current) return

    const instance = monaco.editor.createDiffEditor(host.current, {
      theme: DBDATASYNC_THEME,
      // The whole thing is a view of something already committed. Nothing here edits config; the
      // Pipeline and Columns tabs are where a mapping is changed.
      readOnly: true,
      originalEditable: false,
      automaticLayout: true,
      renderSideBySide: true,
      // A YAML file is deep rather than wide, and the two panes halve the width available — so the
      // long values (a connection string, a transform expression) wrap instead of hiding behind a
      // horizontal scrollbar nobody notices.
      diffWordWrap: 'on',
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      fontFamily: getComputedStyle(document.documentElement).getPropertyValue('--mono') || 'monospace',
      fontSize: 12.5,
      lineHeight: 19,
      lineNumbersMinChars: 3,
      folding: false,
      overviewRulerLanes: 0,
      scrollbar: { verticalScrollbarSize: 9, horizontalScrollbarSize: 9, alwaysConsumeMouseWheel: false },
      padding: { top: 8, bottom: 8 },
    })
    editor.current = instance

    return () => {
      const model = instance.getModel()
      instance.dispose()
      // The diff editor does not own its two models, so disposing it leaks them unless they go too.
      model?.original.dispose()
      model?.modified.dispose()
      editor.current = null
    }
    // Created once; the models below are swapped rather than the editor being rebuilt, so switching
    // between two commits does not flash an empty pane.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    const instance = editor.current
    if (!instance) return

    const previous = instance.getModel()
    instance.setModel({
      original: monaco.editor.createModel(before ?? '', language),
      modified: monaco.editor.createModel(after ?? '', language),
    })
    previous?.original.dispose()
    previous?.modified.dispose()
  }, [before, after, language])

  return <div ref={host} data-testid={testId} className="code-editor" style={{ height }} />
}
