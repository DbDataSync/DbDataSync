import * as monaco from 'monaco-editor/editor/editor.api'
// Each register.js declares its language with a lazy `loader: () => import('./csharp.js')`, so the
// tokenizer arrives as its own chunk when a model first uses it. Registering these two costs almost
// nothing; importing `monaco-editor` wholesale would register all ninety-odd and pull the LSP client.
import 'monaco-editor/languages/definitions/csharp/register.js'
import 'monaco-editor/languages/definitions/sql/register.js'
import EditorWorker from 'monaco-editor/editor/editor.worker.js?worker'

declare global {
  interface Window {
    MonacoEnvironment?: { getWorker: (moduleId: string, label: string) => Worker }
  }
}

/**
 * Only the base editor worker. The TypeScript, JSON, CSS and HTML workers exist for language services
 * we do not use — syntax colouring and server-supplied diagnostics need none of them.
 */
window.MonacoEnvironment = { getWorker: () => new EditorWorker() }

export const DBDATASYNC_THEME = 'dbdatasync'

/**
 * Built from the app's own tokens rather than Monaco's stock `vs`, which is a cold grey-blue against a
 * warm off-white palette and reads as an embedded IDE rather than part of the application.
 */
monaco.editor.defineTheme(DBDATASYNC_THEME, {
  base: 'vs',
  inherit: true,
  rules: [
    { token: 'comment', foreground: '8b8981', fontStyle: 'italic' },
    { token: 'keyword', foreground: '0b5c40' },
    { token: 'string', foreground: '7a5a2e' },
    { token: 'string.sql', foreground: '7a5a2e' },
    { token: 'number', foreground: '3a3833' },
    { token: 'type', foreground: '4d7a66' },
    { token: 'identifier', foreground: '1a1a17' },
    { token: 'operator.sql', foreground: '0b5c40' },
    { token: 'predefined.sql', foreground: '4d7a66' },
  ],
  colors: {
    'editor.background': '#ffffff',
    'editor.foreground': '#1a1a17',
    'editorLineNumber.foreground': '#c3c0b7',
    'editorLineNumber.activeForeground': '#7c7a72',
    'editor.lineHighlightBackground': '#fbfaf8',
    'editor.selectionBackground': '#e2ece7',
    'editorIndentGuide.background1': '#f2efe9',
    'editorGutter.background': '#ffffff',
    'editorWidget.background': '#fbfaf8',
    'editorWidget.border': '#e6e3dc',
    'editorError.foreground': '#a8402f',
  },
})

export { monaco }
