# Phase 28 — A real code editor for SQL and C#

**Status**: Built
**Plan reference**: none — asked for directly. Closes the open question phase 23 left:

> **Where does the SPA editor come from?** A monospace textarea plus compile-on-save diagnostics is
> enough for a first cut; Monaco is a large dependency for a language service the diagnostics mostly
> replace.

That was the right call for a first cut and is the wrong one now. Three phases have landed since, and
the editor is no longer a corner of the app: scripts hold C# (four slots) **and** SQL (phase 26's
reusable hooks), phase 27 generates hook SQL from C#, and phase 22's transforms are SQL too. A
textarea is where a growing amount of the product's actual content is authored.

## What this phase will build

**`CodeEditor`** — a Monaco-backed component, used wherever code is authored:

| where | language | why |
| --- | --- | --- |
| `ScriptEditPage` | `csharp` or `sql`, from the manifest's `Language` | The main event. Multi-line, and the only place with server diagnostics to show. |
| `TableMappingForm`'s source filter | `sql` | A predicate that outgrew its `<input>` — they get long, and the current field shows about forty characters of one. |

**Not** the per-column `Transform` cell in `ColumnMappingEditor`. That is a grid with one row per mapped
column, and a Monaco instance per row would mean forty editors on a wide table — each with its own
model, DOM and layout observer. The expressions there are short by nature. It keeps its compact
monospace input, and this is a deliberate decision rather than an oversight.

## Diagnostics become markers

This is the payoff, and the reason Monaco beats a textarea by more than syntax colouring.

`ScriptCompiler` already reports `(line, column, message)`, and `ScriptsController` already returns them
from both `PUT` and `POST /compile`. Feeding those to `monaco.editor.setModelMarkers` turns a list of
messages under the editor into a red squiggle on the offending token, with the message on hover — which
is what an operator gets from an IDE and could not get from the previous design at any price.

The list stays as well. A squiggle you have to hunt for is worse than a list you can read, and the two
together are what every real editor does.

## Bundling: no CDN, and not the whole of Monaco

Two constraints that rule out the obvious approach.

**`@monaco-editor/react` is not used.** Its default loader pulls Monaco from a CDN at runtime. This is
an operator console that will run on networks with no route to `jsdelivr`, in front of databases that
are not on the internet either. A thin wrapper of our own is about eighty lines and has no such
opinion.

**The import is narrowed.** `import 'monaco-editor'` resolves to `editor.main.js`, which registers
every one of the ninety-odd basic languages and pulls the LSP client. What is imported instead:

```ts
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api'
import 'monaco-editor/esm/vs/languages/definitions/csharp/register.js'
import 'monaco-editor/esm/vs/languages/definitions/sql/register.js'
```

Each `register.js` declares its language with a lazy `loader: () => import('./csharp.js')`, so the
tokenizer itself is a separate chunk that arrives when a model first uses it. Registering two languages
costs almost nothing; registering ninety costs ninety.

**Lazy-loaded.** The whole thing sits behind `React.lazy`, so the replications list — which most
sessions never leave — does not pay for an editor it never shows.

**One worker.** Monaco needs `MonacoEnvironment.getWorker`. Only the base editor worker is needed: the
TypeScript, JSON, CSS and HTML language services are what the other workers are for, and none of them
is in play here. Vite's `?worker` import handles the bundling.

## Theme

The app's palette is a warm off-white with a green accent and no dark mode. Monaco's stock `vs` theme is
a cold grey-blue and would look pasted in. A `datasync` theme defined from the same tokens
(`--surface`, `--ink`, `--accent`, `--danger-ink`) is a dozen rules and makes the editor look like part
of the application rather than an embedded IDE.

## What this phase does not build

Any language *service*. No C# IntelliSense, no SQL completion against the source's catalog, no
formatting. Syntax colouring plus server-side diagnostics is the whole scope. Real C# completion means
running Roslyn's workspace services behind an LSP, which is a project rather than a phase.

## How to verify when built

- Playwright: the existing script tests (15, 16) currently `fill()` a textarea and must keep working
  against Monaco — the editor's hidden textarea takes `insertText`, and the tests should read as
  before.
- A test that a compile error puts a **marker** on the right line, not just a message in the list.
- A test that the source filter still round-trips through a save.
- `tsc -b` clean, `oxlint` no worse.
- The built bundle: Monaco in its own chunk, and the entry chunk not materially larger.

## Open questions

- **Should the SQL editor know the source's columns?** Monaco supports completion providers, and the
  metadata endpoint already returns the columns for both sides of a mapping. That is a genuinely useful
  next step and is not this phase.
- **Read-only diff view for the Version Control tab.** Monaco has one, and phase 6's git history
  currently shows only commit metadata. Worth its own consideration once the editor exists.

---

# Retrospective

Built as planned. The measurements are the interesting part, because the reason phase 23 chose a
textarea was a guess about cost and it turned out to be avoidable.

## The bundle, measured

| chunk | raw | gzip |
| --- | --- | --- |
| `index` (the app) | 395 kB | 114 kB |
| `MonacoEditor` | 2,624 kB | 673 kB |
| `csharp` tokenizer | 4.1 kB | 1.7 kB |
| `sql` tokenizer | 8.8 kB | 3.7 kB |
| `editor.worker` | 300 kB | — |

**The entry chunk contains no Monaco at all** — `grep -c monaco dist/assets/index-*.js` is 0. Everything
above the first row arrives only when a screen shows an editor, which is two screens out of nine.

The narrowed import did what it was supposed to. Importing `monaco-editor` would have registered all
ninety-odd basic languages eagerly; importing the two `register.js` files instead puts each tokenizer in
its own chunk — 4 kB and 9 kB — that arrives when a model first uses that language. A C# script never
downloads the SQL tokenizer.

Phase 23's "Monaco is a large dependency for a language service the diagnostics mostly replace" was
half right and half wrong. It **is** large. But it is 2.6 MB nobody loads unless they open the editor,
and the diagnostics do not replace the language service — they *become* it, once they are markers.

## The specifiers are not the ones the internet tells you

`monaco-editor@0.56`'s `exports` map is `"./*": "./esm/vs/*.js"`, so the widely-copied
`monaco-editor/esm/vs/editor/editor.api` resolves to `esm/vs/esm/vs/...` and fails. The working
specifiers strip the prefix the map already supplies:

```ts
import * as monaco from 'monaco-editor/editor/editor.api'
import 'monaco-editor/languages/definitions/csharp/register.js'
import 'monaco-editor/languages/definitions/sql/register.js'
import EditorWorker from 'monaco-editor/editor/editor.worker.js?worker'
```

Also note `languages/definitions/...`, not `basic-languages/...` — 0.56 moved them, and
`basic-languages/monaco.contribution.js` is now just an index that imports all of them.

## Three things the wrapper has to get right

Each of these is a bug if it is missed, and each is one line:

- **The change handler lives in a ref.** Subscribing in the creation effect and depending on `onChange`
  would tear down and rebuild the subscription on every keystroke's re-render, which is how an editor
  starts dropping input.
- **The value is only written back when it differs.** Pushing `value` in on every render moves the
  cursor to the end on every keystroke.
- **The editor is created once.** Language and markers are pushed into the existing instance;
  recreating it would lose the cursor, the undo stack and the scroll position.

## Markers land on the token, not the character

`setModelMarkers` takes a range, and the obvious `endColumn = column + 1` underlines one character. The
marker now runs to the end of the word under the position
(`model.getWordAtPosition(...)?.endColumn`), falling back to the end of the line — so the squiggle
covers the identifier the compiler complained about.

A diagnostic with no position (line 0, which `ScriptCompiler` emits for "entry type not found" and
similar) is anchored at line 1 rather than dropped, because a marker nobody can see is worse than one
in a slightly wrong place.

## Playwright needed one helper, and no test read differently

Monaco is not an `<input>`, so `fill()` does not reach it. It keeps a hidden textarea, and
`page.keyboard.insertText` writes through it in a single event rather than as keystrokes — which
matters, because keystrokes would trigger auto-closing brackets and auto-indent and mangle a C# script
on the way in.

`setCode(page, testId, code)` is four lines and the three call sites read exactly as they did with the
textarea.

Test 15 gained the assertion that justifies the whole phase: after a deliberate compile error, a
`.squiggly-error` is visible **inside the editor**, not only in the list below it.

## What was deliberately left alone

The per-column `Transform` cell in `ColumnMappingEditor`. It is a grid with one row per mapped column,
and a Monaco instance per row means forty editors on a wide table, each with a model, a DOM subtree and
a layout observer. The expressions there are short by nature — `UPPER({{column}})` — and the compact
monospace input is the right control for them.

The source filter *did* move, because a predicate that narrows a real table outgrows forty visible
characters quickly and is spliced into the reader's WHERE clause verbatim.

## Verification

- Playwright: 16 green, including the three script-editing tests driving Monaco and the new marker
  assertion.
- `tsc -b` clean. `oxlint` unchanged at its four pre-existing `set-state-in-effect` warnings.
- Build output as tabled above; entry chunk verified Monaco-free.
- **No .NET change** — this phase touched no API, contract or type.

## Open questions

- **SQL completion from the source's catalog.** Monaco takes a completion provider and the metadata
  endpoint already returns both sides' columns. The single most useful next step for the SQL editors,
  and cheap now that the editor exists.
- **A diff view for the Version Control tab.** Monaco has one; phase 6's git history shows only commit
  metadata. Worth its own consideration.
- **C# IntelliSense** stays out of scope. It means running Roslyn's workspace services behind an LSP,
  which is a project rather than a phase.
