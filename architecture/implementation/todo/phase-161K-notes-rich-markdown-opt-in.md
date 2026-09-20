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

`DbDataSync:Notes:RichMarkdown` (bool, default `false`) — an app-level key, the same shape as every other
`DbDataSync:*` setting. Per `cli-setup-and-api-parity.md`, it has to be reachable the same way from **Admin
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

- **Restart or live?** If `DbDataSync:Notes:RichMarkdown` is applied live (as some Admin Config keys are),
  the badge tracks it immediately; if it needs a restart, use `RestartRequiredBanner` like the other
  restart-only keys. Decide from how the config layer classifies it.
- **Images in Notes.** A note that embeds a remote image makes every viewer's browser fetch it — a tracking
  and privacy question, not just an XSS one. Leaning: render images in notes as links, not inline, even when
  rich rendering is on, and say so in the warning. Needs a decision before checkpoint 3.
- **Badge shape and exact placement** — the requirement is "persistent and ambient"; the design is not
  fixed.
