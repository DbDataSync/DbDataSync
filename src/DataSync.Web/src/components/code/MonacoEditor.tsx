import { useEffect, useRef } from 'react'
import { DATASYNC_THEME, monaco } from './monacoSetup'
import type { CodeEditorProps } from '../CodeEditor'

/** Where a diagnostic with no position is anchored, so it is still visible rather than silently
 * dropped. Line 1, the whole first token. */
const NO_POSITION = { line: 1, column: 1 }

/**
 * The Monaco instance itself. Only ever reached through `CodeEditor`, which lazy-loads it — importing
 * this module pulls the editor, so nothing that does not show one should touch it.
 */
export default function MonacoEditor({
  value, language, onChange, diagnostics = [], readOnly = false, minLines = 3, maxLines, testId,
}: CodeEditorProps) {
  const host = useRef<HTMLDivElement>(null)
  const editor = useRef<monaco.editor.IStandaloneCodeEditor>(null)
  // Held in a ref so the change subscription never has to be torn down and rebuilt — re-subscribing on
  // every keystroke's re-render is how an editor starts dropping input.
  const onChangeRef = useRef(onChange)
  onChangeRef.current = onChange

  useEffect(() => {
    if (!host.current) return

    const instance = monaco.editor.create(host.current, {
      value,
      language,
      theme: DATASYNC_THEME,
      readOnly,
      automaticLayout: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      fontFamily: getComputedStyle(document.documentElement).getPropertyValue('--mono') || 'monospace',
      fontSize: 12.5,
      lineHeight: 19,
      lineNumbersMinChars: 3,
      folding: false,
      renderLineHighlight: 'line',
      overviewRulerLanes: 0,
      scrollbar: { verticalScrollbarSize: 9, horizontalScrollbarSize: 9, alwaysConsumeMouseWheel: false },
      padding: { top: 8, bottom: 8 },
      tabSize: 4,
    })
    editor.current = instance

    const subscription = instance.onDidChangeModelContent(() => onChangeRef.current(instance.getValue()))
    return () => {
      subscription.dispose()
      instance.getModel()?.dispose()
      instance.dispose()
      editor.current = null
    }
    // Created once. Value, language and diagnostics are pushed in by the effects below, because
    // recreating the editor would lose the cursor, the undo stack and the scroll position.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Only when it actually differs — writing the value back on every render would move the cursor to
  // the end on every keystroke.
  useEffect(() => {
    const instance = editor.current
    if (instance && instance.getValue() !== value) instance.setValue(value)
  }, [value])

  useEffect(() => {
    const model = editor.current?.getModel()
    if (model) monaco.editor.setModelLanguage(model, language)
  }, [language])

  useEffect(() => {
    const model = editor.current?.getModel()
    if (!model) return

    monaco.editor.setModelMarkers(model, 'datasync', diagnostics.map((d) => {
      const line = d.line > 0 ? d.line : NO_POSITION.line
      const column = d.line > 0 && d.column > 0 ? d.column : NO_POSITION.column
      return {
        severity: monaco.MarkerSeverity.Error,
        message: d.message,
        startLineNumber: line,
        startColumn: column,
        endLineNumber: line,
        // To the end of the word under the position, so the squiggle covers the token rather than one
        // character of it.
        endColumn: model.getWordAtPosition({ lineNumber: line, column })?.endColumn
          ?? model.getLineMaxColumn(Math.min(line, model.getLineCount())),
      }
    }))
  }, [diagnostics])

  const lines = Math.max(minLines, Math.min(maxLines ?? Number.MAX_SAFE_INTEGER, value.split('\n').length + 1))

  return (
    <div
      ref={host}
      data-testid={testId}
      className="code-editor"
      style={{ height: lines * 19 + 16 }}
    />
  )
}
