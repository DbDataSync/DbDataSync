# Phase 160 — the docs ship with the app, and the web console can read them (planned)

First of three phases split from `architecture/planning/done/embedded-docs-in-the-app.md` (now resolved into
these). **160K** ships the docs and a viewer. **161K** lets Notes opt in to the same renderer. **162K**
embeds the images, cleans the docs up, and preprocesses them for the NuGet listing.

## Why

`docs/*.md` exists only in git. Neither the nupkg, the container image nor the API project ships it
(verified 2026-09-19: no pack item, `COPY` line or copy target mentions `docs/`). An operator running a
deployed instance — especially an offline one — can only read it on GitHub, with no guarantee that what
`main` says is what they are running. That matters more since phase 158/159: operators can now be on any
snapshot, so "the docs for the version I'm running" is a real question, not a nicety.

Three screens already point at docs that cannot be reached: `AdminConfigPage.tsx` and
`AdminCertificatePage.tsx` say "see docs/configuration.md" as plain text, and `AdminDriversPage.tsx:87`
points an operator at `architecture/planning/todo/nuget-loaded-drivers.md`, an internal design doc that
is not shipped and was never meant for them.

## Design

### Packaging

- A `CopyPrebuiltDocs` target in `DbDataSync.Cli.csproj`, modelled directly on `CopyPrebuiltSpa`
  (`BeforeTargets="Publish"`), copying `docs/*.md` to `$(PublishDir)wwwroot/docs/`. It must land in the
  **packed tool**, not only in a `dotnet publish` output: phase 137 found the nupkg had shipped an empty
  `wwwroot` for a long time because pack and publish are different paths. So the check below inspects the
  packed artifact, not the target.
- A matching `COPY docs/ /app/wwwroot/docs/` in the `Dockerfile`.
- **All seven pages ship, including `development.md`.** It is small, other pages link to it
  (`state-database.md` does), and shipping it means the viewer needs no "this link points at something we
  did not ship" special case. (The renderer still degrades a link to a missing page to plain text, so an
  edit that removes a page cannot produce a dead link.)

### Serving

Raw `.md` as static files under `wwwroot/docs/`, served by the existing `UseStaticFiles()`; the SPA fetches
them. No new controller. `MapFallback` already excludes `/api` and `/hubs`; the docs *route* in the SPA
(`/docs/...`) must not collide with the static path — the viewer's route is `/docs/:page` and the files
live at `/docs/<page>.md`, so the fetch uses the `.md` suffix and the route does not.

### Version — `GET /api/about`

Phase 159 added `RunningVersion` (`UpdateHostFacts`), but it is only exposed under `api/admin/update`, which
is Admin-only. The viewer must work for a Viewer. Add a small `GET /api/about` returning
`{ "version": "<informational version>" }`, readable by any authenticated role, reusing the value
`UpdateHostFacts` already computes rather than reading the assembly attribute a second time. 161K adds the
Notes setting to this same response, which is why it is called `about` and not `version`.

### Rendering — `react-markdown` + `remark-gfm` (decided 2026-09-16)

`Markdown.tsx` (hand-rolled, no tables, no images) stays as it is: it is the XSS-hardened renderer for
operator-authored Notes. A new shared `RichMarkdown` component is used for docs.

- **No `rehype-raw`.** Raw HTML in the source is inert text, never markup, by construction — not by a
  sanitisation pass afterwards.
- **`rehype-slug`** so heading anchors exist. It uses `github-slugger`, the same algorithm GitHub uses, so
  the docs' existing `](drivers-and-libraries.md#descriptor-drivers)` anchors keep working.
- **Scheme allowlist** on links and image URLs — `http:`, `https:`, `mailto:`; anything else is rendered as
  literal text. Reuse the allowlist `Markdown.tsx` already enforces rather than writing a second one;
  `react-markdown` does not police URL schemes on its own.
- **Link rewriting** (missing from the original plan, and it touches nearly every page — the docs link to
  each other relatively): `x.md` → route `/docs/x`, `x.md#a` → `/docs/x#a`, `#a` stays in-page, and
  `../<anything>` or a link to a page that did not ship → plain text. External links open in a new tab with
  `rel="noopener noreferrer"`.
- Images: **left pointing at `raw.githubusercontent.com` in this phase.** `getting-started.md`'s seven
  screenshots are remote today; a broken image degrades gracefully, and 162K owns making them local.

### Web console

- A **Docs** item on the icon rail, visible to every role (`AdminTabs` is Admin-only; docs are not).
  Route `/docs` (index, listing the pages) and `/docs/:page`.
- The page shows the running version ("Docs for 2026.9.20.517-beta") from `/api/about`.
- The three dangling references are fixed: the two "see docs/configuration.md" mentions become in-app
  links, and `AdminDriversPage.tsx:87` points at `drivers-and-libraries.md`.

## What this does not do

- No editing in the app; `docs/*.md` in git stays the only source.
- No full-text search — seven pages is browsable.
- No "compare against latest" — the viewer shows exactly what shipped.
- No image embedding, and no change to the docs' own content or link style (162K).
- No change to how Notes render (161K).

## Checkpoints

1. **`GET /api/about`.** Any authenticated role gets the version; unauthenticated gets what the rest of the
   API gives. Test through the real pipeline, like the other controller tests.
2. **Packaging.** `CopyPrebuiltDocs` + `Dockerfile`. A test (or a `package`-job step in `ci.yml`) that
   unzips the packed nupkg and asserts `wwwroot/docs/*.md` — seven files — are inside. This is the check
   that would have caught phase 137's empty `wwwroot`.
3. **`RichMarkdown` + its tests.** Unit tests, in the renderer's own style: a table renders as a `<table>`;
   raw `<script>` / `<img onerror>` in the source is inert text; `javascript:` and `data:` links and images
   are literal text; `x.md#a` becomes `/docs/x#a`; a link to an unshipped page is plain text.
4. **Viewer route + rail item + the three reference fixes.**
5. **Playwright.** A Viewer-role user can open `/docs` and a page containing a table; the version shows;
   an in-doc cross-link navigates to another doc and its anchor.
6. **Verify against a real packed tool**: `dotnet pack`, install into a scratch tool root, run it, open
   the docs. Done means this was actually done, not inferred from the target.

## Open questions

- Must `/api/about` be readable before sign-in (so a sign-in page could show the version)? Leaning no —
  authenticated only, like the rest of the read-only API — but nothing needs it either way.
- Route shape: `/docs/:page` with the slug being the file stem is the simple choice. Confirm the index
  page's ordering (the README lists Install, Configuration, Getting started, Replication concepts, Drivers
  and libraries, State database, Building from source) — leaning to reuse that order.
- The docs are packaged **as authored**. If 162K's preprocessing changes their link style, the embedded copy
  and the GitHub copy could diverge; 162K keeps one authored form and transforms at build time, so this
  phase's renderer must not depend on any particular link style beyond "relative `.md`".

## Progress

- [x] **1. `GET /api/about`** — `AboutController` (`Policies.Viewer`, so Viewer and Admin), returning
  `{ version }` from `UpdateHostFacts.RunningVersion`. Tests: both roles read the version; anonymous gets 401.
- [x] **2. Packaging.** `CopyDocs` target in `DbDataSync.Cli.csproj` (unconditional on `dist/`; conditional on
  `docs/` existing), a `COPY docs/ /app/wwwroot/docs/` line in the `Dockerfile`, and two `ci.yml` assertions:
  the packed nupkg contains every `docs/*.md` (compared to `docs/` itself, not a count), and the running default
  container serves each one. **Verified by a real `dotnet pack`** — all seven pages are under
  `tools/net10.0/any/wwwroot/docs/`. **Not verified locally:** the Dockerfile line and the container assertion
  (no docker here) — first exercised by CI.
  - *Corrected assumption:* the plan expected `.md` to need a content-type mapping; .NET 10's default table
    already has `.md` → `text/markdown` (probed before shipping code for it), so no host change was made.
    `EmbeddedDocsServingTests` pins what the viewer depends on instead: a page is served as Markdown, `/docs` and
    `/docs/<page>` are the SPA's routes, and a missing `.md` is a real 404 (`MapFallback` skips file-shaped
    paths, so the viewer never gets the app's HTML to render as a document).
  - Note: static files are served before authentication, like the SPA's own assets, so the docs are readable
    without signing in. They are the same text that is public on GitHub.
- [x] **3. `RichMarkdown`, its link policy and its tests.** `components/RichMarkdown.tsx` (`react-markdown` +
  `remark-gfm` + `rehype-slug`, no `rehype-raw`, `urlTransform` the identity so one policy decides), and
  `components/markdownLinks.ts`, which `Markdown.tsx` now shares for its own allowlist rather than keeping a second.
  The SPA had no unit-test runner, so this adds **vitest** (`npm test`, a CI step in the `web` job): components are
  rendered with `react-dom/server` in plain Node — enough to assert what markup a document may and may not produce.
  52 tests: tables, task lists, GitHub-compatible heading ids; raw `<script>`/`<img onerror>`/`<iframe>`/`<svg onload>`
  inert; `javascript:`/`data:`/`vbscript:`/`//host` links and whitespace/control-character disguises never become an
  `href`; images from network addresses only, lazy, no referrer; `x.md#a` → `/docs/x#a`. Mutation-checked: making
  the policy allow everything fails 33 tests.
  - **`docPages.ts` and its drift test.** The list of shipped pages (with titles, in README order) is checked against
    `docs/*.md` at test time, and so is every relative `.md` link inside the docs — a renamed or removed page fails a
    build rather than a reader.
  - **Found while writing the link check:** every page opens with a nav line whose first link is
    `[DbDataSync](../README.md)`. That resolves to the docs index (`/docs`) in the app.
- [x] **4. Viewer route, rail item, and the three reference fixes.** `DocsPage` at `/docs` (index) and
  `/docs/:page` (a list of pages beside the page, the running version from `/api/about`, a message for a page that is
  not one of the seven); a **Docs** rail item for every role (`BookIcon`); `api.about` / `api.docs.page` /
  `useAbout` / `useDocPage`. The fetch refuses a response that is not `text/markdown`, so an HTML error page from a
  proxy is never rendered as a document. A `/docs/x#section` link scrolls to its heading once the text arrives (the
  router does not do that by itself). The Vite dev server serves the repo's `docs/` at `/docs/*.md` through a small
  plugin, so the viewer works unchanged in dev. The three dangling references are fixed: `AdminConfigPage` and
  `AdminCertificatePage` link to `/docs/configuration`, and `AdminDriversPage` now links to
  `/docs/drivers-and-libraries#descriptor-drivers` instead of naming an internal planning doc.
- [x] **5. Playwright** (`docs-viewer.spec.ts`, 8 tests, passing locally against the real API and the Vite dev
  server): the rail leads to an index of all seven pages and the running version; a page with a table renders as a
  table; a cross-page link with an anchor stays in the app and scrolls to its heading; the README link every page
  opens with goes to the index; an unknown page says so; the three admin references are real links; a hostile
  document (`<script>`, `<img onerror>`, `javascript:` link and image, `<iframe>`) is shown as text, raises no
  dialog and sets nothing on `window`; an HTML answer in place of Markdown is refused. Two screenshots are committed
  under `screenshots/docs-viewer/`.
  - **Not tested here:** a Viewer-role user. The suite runs with authentication off (the trusted-network mode), so
    there is no Viewer to sign in as; `AboutControllerTests` covers that a Viewer may read the version, and nothing
    on the Docs screens branches on role.
- [x] **6. Verified against the real artifacts** — and it found a bug that predates this phase.
  - **A real `dotnet pack`**: all seven pages under `tools/net10.0/any/wwwroot/docs/`. **A real `docker build`**: the
    image has `/app/wwwroot/docs/*.md`, and each served page is byte-identical to `docs/`.
  - **The bug:** in that container, with authentication on (the default), `/`, `/invite`, `/replications/…` and every
    asset answered **401 with an empty body** — as does the released `2026.9.18.1918`. The closed-by-default fallback
    policy applied to static files because `UseStaticFiles` and the SPA `MapFallback` were after `UseAuthorization`, so a
    default deployment could not serve its own sign-in screen. The docs viewer would have been unreachable with it.
    **Fixed here:** static files moved ahead of authentication/authorization; the SPA fallback is `.AllowAnonymous()`
    (unmatched `/api` and `/hubs` paths still 404, never the page). `EmbeddedDocsServingTests` now runs with
    authentication **on** and asserts, for someone who has not signed in: the app and its deep links load, the docs are
    served, `/api/*` is still 401. Re-verified in a real container. No API test could see this before — they all run
    with authentication off, as does Playwright.
  - **A gap in CI, recorded rather than fixed:** `ci.yml`'s `package` job (the only place the image is built) runs only
    on `release/v*` tags, which nothing pushes any more, so **no workflow builds the Dockerfile**; and its "serves the
    web console" check was vacuous (`curl -sf | grep -q` passes on a 401). The check is fixed in text; the job still
    doesn't run. See `architecture/planning/todo/follow-up-phase-160-ci-never-builds-the-dockerfile-and-its-package-job-never-runs.md`.
    The docs-in-the-nupkg assertion therefore also went into `release.yml` (before it publishes) and
    `publish-snapshot.yml`, where it does run — the shell logic was run against the real nupkg.
