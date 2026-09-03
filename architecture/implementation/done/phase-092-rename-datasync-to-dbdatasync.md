# Phase 92 — Rename DataSync to DbDataSync, everywhere current

**Status**: Complete.
**Plan reference**: none upstream in `architecture/planning/` — a direct product request (rename the
project), resolved through one clarifying question in conversation on 2026-09-02 rather than a planning
doc, since there was no unresolved rough thought preceding it, only a scope boundary to settle.

## What this was

A pure, mechanical, solution-wide rename: `DataSync` → `DbDataSync` in every casing variant, across every
current file in the repository — directories, project files, namespaces, class/const names, the CLI's own
identity, the configuration surface (section names, the config file's own name, secret-ref prefixes, env
vars), the SPA, Docker/CI, and every current (non-`done/`) doc. No behavior changed anywhere; the diff is
718 files (2,933 insertions / 2,933 deletions in line content, on top of 679 renames git's similarity
detection recognized as such — a handful of small files whose content changed enough relative to their
size fell under the detection threshold and show as an add+delete pair instead, which is a `git diff`
display heuristic, not a data-loss concern).

## What was renamed

- **Directories and project files**: every `src/DataSync.*`, `tests/DataSync.*.Tests`, `tools/DataSync.*`
  directory and the `.csproj` inside it, via `git mv`. `DataSync.slnx` → `DbDataSync.slnx`, every
  `<Project Path>` and `<ProjectReference Include>` updated to match.
- **Namespaces and code**: every `namespace DataSync.X`/`using DataSync.X;` and every fully-qualified
  reference, across every `.cs` file. Every branding identifier — `DataSyncHost`, `DataSyncConfigFile`,
  `DataSyncRoot`, `DataSyncConfigFileSource`, `DataSyncConfigFileProvider`, and the parallel test-class
  names — renamed to its `DbDataSync`-prefixed equivalent.
- **CLI identity**: `ToolCommandName` (`datasync` → `dbdatasync`), `PackageId` (`DataSync` →
  `DbDataSync`), `CliOptions.DefaultRoot`'s per-user folder name, `ServiceCommand`'s `ServiceName`/
  `DisplayName`, and every usage/help string in `Help.cs`/`Program.cs`.
- **Configuration surface**: every `configuration.GetSection("DataSync"...)` call (`ApiOptions`,
  `AuthOptions`, `PasskeyOptions`, `CertificateOptions`), every documented `DataSync:*`/`DataSync__*` key,
  `DataSyncConfigFile.FileName` (`datasync.config.yaml` → `dbdatasync.config.yaml`, including its starter
  content constant), `SecretRefs`'s `datasync:connection:*`/`datasync:config:*` prefixes, the
  `DATASYNC_STATE_ENDPOINT` env var, the `datasync-repo` dev-time default folder name and its `.gitignore`
  entry, and the admin config screen's full key catalog in `AdminConfigService.cs`.
- **Frontend**: `package.json`'s `name`, the app shell's brand mark, the `<title>`, sign-in/invite copy,
  the Monaco editor theme name/id, and every other UI-visible or comment string the SPA source held.
- **Docker/CI**: `Dockerfile` (`WORKDIR`, `COPY` paths, `ENV DATASYNC_HOME`, the volume path, the
  entrypoint DLL name), `.dockerignore`, both `docker-compose*.yml` files, `.github/workflows/ci.yml` and
  `release.yml` (container tags, tool-install commands, release title/notes).
- **Docs**: `README.md`, `CONFIG.md`, `architecture/detailed-design.md`, `architecture/implementation-plan.md`,
  `architecture/implementation/README.md` (only the prose and the one filename mentioned in its build-order
  table — the table's structure, ordering, and phase numbers were left untouched, per the ground rule),
  the three `todo/` phase docs (034, 035, 038), and the two `architecture/planning/todo/*.md` docs that
  happened to mention the product name in ordinary prose (`change-tracking-mysql.md`,
  `mssql-cdc-source-batching-and-guaranteed-delivery.md`).
- **Two files this session's own tooling missed on the first pass, found only by re-running the grep sweep
  with a corrected pattern (see "A methodology bug, caught and fixed" below)**: seven `.cs` files whose
  *filename* still said `DataSync` even though their class body had already been renamed inside —
  `DataSyncHost.cs`, `DataSyncConfigFileSource.cs`, `DataSyncRoot.cs`, `DataSyncConfigFile.cs`, and the
  three parallel test files (`DataSyncConfigFilePrecedenceTests.cs`, `DataSyncRootTests.cs`,
  `DataSyncConfigFileTests.cs`). Directory-level `git mv` renamed the *folders*; nothing in that pass ever
  renamed an individual source file to match a class whose name changed only via content substitution.
  Fixed with seven more `git mv`s once found; a full rebuild and test re-run afterward showed zero change
  in outcome, confirming these were pure filename housekeeping.
- The starter `dbdatasync.config.yaml` content (phase 79's hardcoded string constant) and the admin config
  screen's key catalog (phase 81's hardcoded list) — the two places the phase doc specifically flagged as
  likely to silently keep working while being quietly wrong — were checked directly and found correctly
  renamed by the mechanical sweep; no additional fix needed there.

## What was deliberately left `DataSync`-named, and why

- **The ~96 historical retrospectives** in `architecture/implementation/done/` and
  `architecture/planning/done/` — untouched, per this repo's "nothing is ever deleted, history isn't
  rewritten" convention.
- **The GitHub repository and its remote URL** (`github.com/danshryock/DataSync`) — explicitly out of
  scope; renaming it is a separate operational decision for whoever owns that account.
- **One concurrently-authored planning doc**,
  `architecture/planning/todo/clrkernel-secrets-0.11-prefix-config.md`. Three commits landed on this
  branch mid-implementation (from what appears to be a parallel session planning the next phase, a
  `ClrKernel.Core.Secrets` package upgrade) — this doc's entire point is a **before/after comparison** of
  the secret-ref prefix string: it documents `"datasync:"` as the *old* value and resolves `DbDataSync`/
  `dbdatasync:` as the *new* one, explicitly to decide there is no migration between them. Running this
  phase's mechanical substitution across that file would have turned "secrets stored under the old
  `datasync:` names are not carried forward" into "...under the old `dbdatasync:` names...", making the
  old and new prefixes textually identical and erasing the sentence's meaning. This one file was left
  completely untouched rather than partially edited by judgment call. The doc itself already says it
  needs re-verification once phase 92 lands ("re-check the state of the rename once it's landed here");
  whoever picks up that phase should read it now that this one is done — the actual code-level rename it
  was waiting on (`SecretRefs.cs`'s prefix) is already complete, which resolves that doc's open question 2.

## How this was verified

- **Build**: `dotnet build DbDataSync.slnx -c Debug` — 0 errors throughout, warning count and content
  identical to a pristine baseline build (13 warnings, all pre-existing and unrelated to this change).
- **Tests, compared against a genuinely clean-`HEAD` baseline**: a separate `git worktree add` checkout
  (not just "the tree before I started," since builds/tests were already in flight against the working
  tree when this phase began — see the pitfall below) was used to run the full suite unmodified, then the
  identical suite was run against the fully-renamed tree.
  - `Category!=Integration`: baseline 279 failed / rename 275 failed at the raw count level, fully
    reconciled to **zero real difference**: 4 of those were `PipelineStatementTests`/
    `WatermarkStatementTests`/`MsSqlChangeTrackingStatementTests` cases that only failed in the *baseline*
    worktree, traced to `core.autocrlf=true` converting those files' LF line endings to CRLF on that
    worktree's fresh checkout (confirmed directly with `file` — the long-lived `main` tree's copies are
    LF, the freshly-checked-out baseline copies are CRLF), which breaks a couple of tests asserting exact
    generated-SQL text against a multi-line raw string literal. Unrelated to the rename; the same 4 would
    fail in any brand-new checkout of the pre-rename code. The other 2 were the same test
    (`AdminConfigControllerTests.AViewer_IsRefusedByAllThreeEndpoints`) with a different-looking display
    name because its parameterized test data *is* a config key path
    (`/api/admin/config/DataSync%3AUrl` → `/api/admin/config/DbDataSync%3AUrl`) — same test, same FAIL
    outcome, expected per the phase doc's own allowance for test-name changes. Every other failing/passing
    test, by exact name, matched 1:1 between baseline and renamed runs.
  - `Category=Integration`: 212 failing tests by name, identical set, identical count, on both sides —
    all connection-refused against a SQL Server/Postgres this sandbox doesn't have running, exactly as
    prior phases in this session recorded.
  - Re-ran `Category!=Integration` a second time after the seven late file renames above; identical
    failing-test set both times, confirming the file renames changed nothing behavioral.
- **SPA**: `npx tsc --noEmit -p tsconfig.json` clean; `npm run build` clean (same chunk-size warning any
  build of this SPA produces, unrelated to this change).
- **Grep sweep**, done twice. The first pass used `grep -vi dbdatasync` to filter matches of `datasync`
  case-insensitively — and, as a **methodology bug found and fixed mid-phase**, that approach silently
  hides a genuine leftover `DataSync` substring whenever it shares a *line* (in this case, a file *path*)
  with an already-correct `DbDataSync` elsewhere on that same line, because `grep -v` discards the whole
  line, not just the matched substring. Every file under `src/DbDataSync.*/` or `tests/DbDataSync.*/`
  satisfies that condition trivially, which is exactly how the seven stale filenames above went
  undetected on the first sweep. Rewritten as a single-pass negative-lookbehind
  (`grep -P '(?<!Db)DataSync|(?<!db)datasync|(?<!DB)DATASYNC'`) applied to both `git ls-files` (path
  names) and file contents, re-run after the fix: the only remaining hits, in either check, are the 17
  lines in the one deliberately-excluded planning doc above. Nothing else in the tracked tree, current or
  historical-exclusion-respecting, still says `DataSync` outside that file.
- Confirmed no `dataSync`/`dbDataSync` camelCase variant exists anywhere in the SPA source (there never
  was one to begin with).

## A process pitfall worth flagging for future large renames in this repo

Kicking off a full-suite baseline test run against the working tree, then starting the directory `git mv`s
in the same tree before that run finished, produced a corrupted, unusable "baseline" (project references
half-updated mid-build). The fix was to discard both runs, shut down lingering `dotnet build-server`
processes (which were also the reason a couple of `git mv`s failed with `Permission denied` — an MSBuild
node with `nodeReuse:true` was holding a directory handle open) and switch to a `git worktree add
<path> HEAD` sitting next to the real work, so the baseline and the in-progress rename never share a
working tree. That worktree was removed once the comparison was captured.

## What this phase did not do

- No dual-name compatibility of any kind — an admin with an existing `datasync.config.yaml`, a
  `DataSync__*` env var, a `datasync:` secret ref, or a Windows service registered as `DataSync` needs to
  reconfigure by hand after this lands. Deliberate, per the original resolution.
- No GitHub repository rename.
- No `done/` doc touched.
- No behavior change, no unrelated cleanup — the two things this phase's own diff should not need any
  explanation beyond "renamed X to Y" for are the seven late file renames (still just a rename, done late)
  and the deletion of one stray, untracked, already-`.gitignore`d dev-run artifact directory
  (`src/DataSync.Api/datasync-repo/`, leftover local state from a prior manual test run, never part of
  source) that would otherwise have sat on disk under its old name forever, invisible to git either way.

## Left for the coordinating session

Everything is staged/unstaged in the working tree, not committed, per instruction — this is a 718-file
diff and warrants a human or coordinating-session look before it lands. `git status` is clean modulo this
phase's own changes plus the three commits (`7e90708`, `be9e493`, `23b86f5`) that landed on `main` from
elsewhere during implementation, which this phase did not touch.
