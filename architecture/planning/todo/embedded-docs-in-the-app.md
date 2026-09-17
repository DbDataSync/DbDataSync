# Embedded docs in the app

## The problem

`docs/*.md` (`install.md`, `configuration.md`, `getting-started.md`, `replication-concepts.md`,
`drivers-and-libraries.md`, `state-database.md`, `development.md`) exists only in the git repo today —
confirmed directly, not assumed: neither `DbDataSync.Cli.csproj`'s pack items, nor `DbDataSync.Api.csproj`,
nor the `Dockerfile`'s `COPY` list references `docs/` anywhere. The nupkg and the container image both
ship without it. An operator running a deployed instance — especially an offline or air-gapped one —
has no way to read any of it except by going to GitHub, and has no guarantee the copy on GitHub's
`main` branch matches the version they're actually running.

The app already half-wants this and can't have it. Two screens already tell an operator to go read
`docs/configuration.md` — `AdminConfigPage.tsx` ("See docs/configuration.md for the full reference"),
`AdminCertificatePage.tsx` ("see `docs/configuration.md`") — as plain, unlinked text, because there's
nothing to link *to*. A third, `AdminDriversPage.tsx`, points at
`architecture/planning/todo/nuget-loaded-drivers.md` — an internal design doc that was never meant for
an operator and isn't shipped either; a real, pre-existing bug this plan would also fix in passing.

## What exists to build on

- **Static file serving is already wired up.** `DbDataSyncHost.cs` already runs `UseStaticFiles()` +
  `MapFallback` for the SPA's own `wwwroot`, with the API's own routes (`/api`, `/hubs`) deliberately
  excluded from the fallback. Shipping docs as static files alongside the SPA reuses this exactly — no
  new hosting mechanism needed.
- **A nav pattern built for exactly this.** `AdminTabs.tsx`'s own doc comment: "adding a destination
  later means one edit, not every page staying in sync by hand."
- **Markdown rendering exists, but the wrong shape for this.** `components/Markdown.tsx` is a
  deliberately minimal, hand-rolled renderer (no `dangerouslySetInnerHTML`, no external library,
  restricted link schemes) built for one thing: an operator's own short, git-tracked mapping/replication
  *Notes* — stored input, rendered in someone else's session, so it's XSS-hardened by construction. It
  supports headings/emphasis/code/lists/quotes/rules — **no tables, no images**. Every doc page just
  written this session (`replication-concepts.md`, `drivers-and-libraries.md`, `state-database.md`) is
  full of tables. This component cannot render them as written today.
- **No JSON round-trip / version endpoint exists yet.** The CLI's `dbdatasync version` reads
  `AssemblyInformationalVersionAttribute` off its own assembly — the one existing precedent for "what
  version am I" — but nothing exposes this over HTTP, and the SPA has zero awareness of its own running
  version anywhere today (no footer, no build-time stamp, no version call).
- **No prior art or rejected proposal.** Nothing in `architecture/planning/` or `architecture/implementation/`
  discusses an in-app docs viewer. The one adjacent, still-unagreed doc (`app-and-service-setup.md`)
  treats `docs/getting-started.md` as something a *terminal* command points a reader at — reference
  material read externally, never rendered in the web console. This plan extends that model into the
  web UI for the first time, rather than reviving or conflicting with anything already discussed.

## Design proposal (leaning, not agreed)

1. **Packaging.** Copy `docs/*.md` into the built artifact at pack/publish time, the same way
   `DbDataSync.Cli.csproj`'s `CopyPrebuiltSpa` target already copies the SPA's built `dist/` into
   `wwwroot` — a `CopyPrebuiltDocs`-shaped target, and a matching `Dockerfile` `COPY docs/ /app/wwwroot/docs/`
   line for the container image. One new, small mechanism, modeled directly on one that already exists
   and already runs at the right point in the build.
2. **Which pages ship.** `docs/development.md` is a contributor doc — building DbDataSync from source —
   useless to someone running the compiled tool. Leaning: ship the other six, exclude that one. Open
   question below.
3. **Serving.** Ship the raw `.md` files as static assets under `wwwroot/docs/`, fetched directly by the
   SPA — no new API controller needed purely to serve bytes that `UseStaticFiles` already knows how to
   serve.
4. **Rendering.** `Markdown.tsx`'s restricted subset can't render these pages as written (no tables). Two
   real options, genuinely open:
   - Extend `Markdown.tsx` itself to add table/image support, keeping its existing no-`dangerouslySetInnerHTML`,
     restricted-link-scheme posture.
   - Bring in a real markdown library (e.g. `react-markdown` + `remark-gfm`) scoped to a new Docs viewer
     only — leaving `Markdown.tsx` exactly as it is for Notes.

   Leaning toward the second: these threat models are genuinely different. `Markdown.tsx` exists because
   Notes are *operator-typed, stored input rendered in someone else's session* — real stored-XSS risk.
   Embedded docs are *developer-authored content baked into the build*, no different in trust level from
   the SPA's own JavaScript. Hand-extending a note-renderer to full CommonMark-with-tables is real,
   avoidable engineering effort for a problem a maintained library already solves — the safety
   justification for reinventing it doesn't carry over.
5. **Navigation.** A new top-level nav destination, not under Admin — `AdminTabs`' four destinations are
   all Admin-only, and docs should be readable by a Viewer-role user too. Where exactly (its own route,
   or folded into an existing non-admin area) is open.
6. **Version matching.** Since docs are packaged at build time alongside the binary, they're automatically
   "this version's docs" with no live-fetch, no version-mismatch risk, and no network dependency — this
   is the reason to embed rather than just linking to GitHub. Exposing the CLI's own
   `AssemblyInformationalVersionAttribute` read over a new small API endpoint (useful independently — an
   About screen has wanted this all along and nothing currently provides it) lets the Docs viewer (and
   anything else) show which version's docs are on screen.
7. **Fix the two existing dangling references while here.** `AdminConfigPage.tsx`/`AdminCertificatePage.tsx`'s
   plain-text `docs/configuration.md` mentions become real in-app links once there's somewhere to link to;
   `AdminDriversPage.tsx`'s broken pointer at an internal, unshipped planning doc gets corrected to point
   at the real, shipped `drivers-and-libraries.md`.

## What this does not do

- **Not editable in the app.** `docs/*.md` in the git repo stays the one source of truth; this is a
  read-only, packaged copy, not a second place documentation gets written.
- **Not a fork of the GitHub-hosted docs.** Same content, same source files, just also available without
  network access and pinned to the exact running version.
- **Not full-text search**, at least not in a first version — seven pages is small enough to browse; add
  it later if the docs set grows enough to need it.
- **Not a live version-mismatch resolver.** An operator looking at embedded docs always sees exactly what
  shipped with their running instance — there is no "compare against latest" feature here.

## Open questions (UNDECIDED)

- **Extend `Markdown.tsx` vs. adopt a real markdown library for the Docs viewer** — leaning toward
  adopting one (see above), but this is a new runtime dependency and worth a deliberate decision, not a
  default.
- **Where in navigation** — leaning toward a new top-level destination outside Admin (Viewer-visible),
  exact placement undecided.
- **Screenshots.** `getting-started.md`'s images are hosted on `raw.githubusercontent.com`, not local
  files — embedding the page as-is means those images still need network access even once the text is
  offline-available. Leaning: leave them pointing at GitHub for now (the text is the part worth having
  offline; a broken image degrades gracefully) rather than doubling the packaging cost to also embed
  screenshots — but this is a real gap in "offline" worth being honest about, not a full solution.
- **Which pages ship** — leaning to exclude `development.md` (see above), confirm before building.
