# Phase 161 — Notes can opt in to the rich Markdown renderer (planned)

Second of three phases split from `architecture/planning/done/embedded-docs-in-the-app.md`. **Must follow
160K**: it reuses 160K's `RichMarkdown` component and 160K's `GET /api/about`.

## Why

`NotesPanel.tsx` renders operator-authored notes with `Markdown.tsx`, a deliberately minimal renderer:
headings, emphasis, code, lists, quotes, rules — no tables, no images. That is the right default, because a
note is stored input written by one user and rendered in *other* users' sessions, and `Markdown.tsx` is
hardened by having almost nothing in it. But operators who want a table in a note have no way to get one.

## Design

Rich rendering for Notes is **a deployment-wide, explicit opt-in, off by default** — never silent,
never per-note.

### The setting

**`DbDataSync:NotesRichMarkdown`** (bool, default `false`) — an app-level key, the same shape as every other
`DbDataSync:*` setting. *(Renamed from the planned `DbDataSync:NotesRichMarkdown` — see "Key name" under Progress: a nested key could never be edited from Admin Configuration.)* Per `cli-setup-and-api-parity.md`, it has to be reachable the same way from **Admin
Config (web), `setup`, and the CLI**, not added to one surface as an afterthought — so this phase includes
all three, and `docs/configuration.md` documents it (the Admin Config screen lists every key that page
documents, so a key missing from either would be visible as drift).

### The renderer

`NotesPanel` chooses `RichMarkdown` when the setting is on and `Markdown.tsx` otherwise. **The same
`RichMarkdown` 160K built, with the same defenses** — no `rehype-raw`, the same scheme allowlist, the same
link handling. There is no second, weaker sanitisation path for Notes. The opt-in is about *exposure* —
whether stored multi-author content reaches the richer renderer at all — not about a different
implementation.

Two things differ from the docs use, both about *where the links go*: Notes are not the app's own pages,
so relative `.md` links are not rewritten to `/docs/...` (they are plain text), and images in a note follow
the same allowlist but load from wherever the author pointed them — see the open question.

### How the SPA learns the setting

Viewers see Notes too, so it cannot come from an Admin endpoint. Add `notesRichMarkdown` to 160K's
`GET /api/about` response (the reason that endpoint is called `about`, not `version`).

### The warning is persistent, in two places

1. **Where it is toggled** (Admin Config; the matching `setup` prompt and CLI help): plain-language text
   saying that notes are operator-authored, stored, and rendered in other users' sessions, and that richer
   rendering widens that surface even with strong defenses — a convincing crafted link, a future library
   issue. A real, bounded trade-off, not a solved problem.
2. **Wherever Notes are shown while it is on:** a small ambient badge near the panel, for as long as the
   setting is on. Not a one-time confirmation that is forgotten by the next session.

## What this does not do

- Does not make rich rendering the default, and does not change what `Markdown.tsx` renders.
- Does not add a Notes-specific sanitiser.
- Not per-replication or per-mapping.
- Does not change how Notes are stored (git-tracked, as now).

## Checkpoints

1. **The key**, in the config model, `docs/configuration.md`, Admin Config, `setup`, and the CLI, with the
   parity test that already guards those surfaces extended to include it.
2. **`/api/about`** carries the flag (test: Viewer sees it, matches the config).
3. **`NotesPanel`** switches renderer on the flag; component tests for both states, including that with the
   flag *off* a table in a note is exactly what it renders today.
4. **The two warnings.** Toggle-time text; the ambient badge, present iff the flag is on.
5. **Security tests specific to Notes**: a crafted note (`<script>`, `<img onerror>`, `javascript:` and
   `data:` links, a link whose text looks like a different URL) with the flag on renders inert, using the
   shared renderer's own defenses.
6. **Playwright**: flag off → table shows as text; flag on → a table, plus the badge.

## Open questions

*(All three settled while building — see Progress.)*

- **Restart or live? — restart.** The setting is read into `ApiOptions` at startup like every other `DbDataSync:*` key, and
  `/api/about` reports the value the process is *running with*, so the badge follows the running process and the Admin screen's
  restart banner covers the gap. No live re-read was added.
- **Images in Notes? — links, not inline**, even with rich rendering on: every reader's browser would otherwise fetch an
  address the author chose the moment the note rendered (tracking and privacy, not only XSS). Said in the warning.
- **Badge shape and placement — a small "Rich Markdown on" pill in the Notes card header**, with a tooltip saying who turned
  it on and what it does; present iff the running setting is on.

## Progress

- [x] **Key name: `DbDataSync:NotesRichMarkdown`, flat.** The plan said `DbDataSync:Notes:RichMarkdown`, but Admin
  Configuration's catalog states that only top-level `DbDataSync:<Key>` keys are file-writable — `DbDataSyncConfigFile`'s
  writer does not address nesting (which is why every `Auth:*` row is read-only). A nested name would have shown on the
  screen and never been editable, defeating "reachable from all three surfaces". Flat matches `NuGetSearchEnabled` and
  `SelfUpdateEnabled`.
- [x] **The setting, one catalog, three doors.** `ApiOptions.NotesRichMarkdown` (default off);
  `AdminConfigService`'s catalog entry carries a new `Caution` (also on the `AdminConfigEntry` DTO, `null` for every other
  key); `AdminConfigService.Writable(key)` exposes the catalog's writable keys, defaults and cautions statically so the
  other two surfaces read the same source instead of a copy:
  - **Admin Configuration (web):** the row, with the caution on its own full-width line — first placed in the narrow key
    column, where a screenshot showed it cut off mid-sentence, which is worse than no warning.
  - **CLI:** `dbdatasync config get|set <key> [value] [--repo]` (new). Knows no keys of its own; refuses what the screen
    refuses (unknown, nested); validates on/off values; prints the caution before enabling; commits with the same message
    the screen uses. This is deliberately a *general* `get`/`set` over the catalog, not a Notes-only verb — the parity
    doc's complaint was that settings existed on one surface only, and every catalog key now has the CLI door.
  - **`setup`:** a checkbox on the General tab with the caution under it (always visible), `Populate`d from the
    configuration and written by `SetupSteps.ApplyNotesRichMarkdown` — ticked writes `true`; unticked writes `false` only
    if the file already mentions the key, so a save on an install that never touched it adds no line.
- [x] **`/api/about`** carries `notesRichMarkdown` (the running value), readable by a Viewer — tested with a Viewer against
  a deployment that turned it on.
- [x] **`NotesPanel`** picks `RichMarkdown` when the flag is on and `Markdown.tsx` otherwise; with the flag off, or unknown
  (still loading, or the call failed), it is the small renderer — the safe one is the default, not whichever loads first.
  `RichMarkdown` gained `inlineImages` (off for Notes: an image shows as a link). No relative-link resolution for Notes.
- [x] **Both warnings.** At the toggle: the caution in Admin Configuration, under the `setup` checkbox, and before the write
  in the CLI — all the catalog's own text. Ambient: the "Rich Markdown on" pill in every Notes card while it is on.
- [x] **Tests.** API: catalog entry (default, caution on this key only, flat/writable, `Writable` lookup), `/api/about` flag
  for a Viewer on and off. CLI: `config get|set` (writes and commits, warns on enable but not disable, normalises `TRUE`,
  rejects non-boolean / unknown / nested keys, refuses a folder with no configuration, `get`), `SetupSteps` (three cases),
  `GeneralTab` (real Space keystroke, `Populate`, warning wrapping). Web: 5 more vitest cases for Notes' use of the renderer
  (57 total); a stubbed Playwright spec (`notes-rich-markdown.spec.ts`, 5 tests): off → text and no badge; on → table and
  badge; on with a hostile note → inert, image is a link and is never requested, no dialog; `/api/about` failing → safe
  renderer; Admin row with its caution. Screenshots under `screenshots/notes-rich-markdown/`.
