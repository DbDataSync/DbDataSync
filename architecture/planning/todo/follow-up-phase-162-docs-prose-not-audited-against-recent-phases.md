# The docs' prose has not been read against phases 145–164

**Found** 2026-09-20, closing phase 162, whose audit was deliberately mechanical.

## What was and was not checked

Phase 162 compared the docs with the code for the things a program can compare: every `DbDataSync:*` key the Admin screen lists has a row
in `configuration.md` (`EveryKeyTheAdminScreenListsIsDocumentedInConfigurationMd`), every `dbdatasync <command>` shown in code is a command
the CLI's help lists (`DocsCommandDriftTests`), every relative link and picture resolves (vitest), and every `--flag` the docs show is a
literal somewhere in the source. That found real problems (commands that no longer existed).

It cannot say whether an **explanation is still true**. Two pages were written before a run of behaviour changes and have not been
re-read against them:

- **`replication-concepts.md`** (256 lines; ~50 table rows): reader kinds, the four run kinds, bulk loading, reconciliation, "why some
  readers' first pass becomes a bulk load". Phases since: 145 (set-based SCD2 duplicates), 147–148 (MySQL/MariaDB and Oracle drivers, trigger
  audit, Flashback), 150–155 (planned or in flight), and any change to which reader kinds exist or how a first pass is chosen.
- **`state-database.md`** (128 lines): which engines back the state store, what is stored, retention. Phases since: retention settings
  (`RunRetention*`, `ChangeCheckRetentionDays`), the pause history, the Updates state under `<data>/updates/`, and phase 164's key
  reorganisation (`State:*`, `State:Retention:*`), which renamed most of what this page names.

Phase 164 has since regrouped every config key; its own docs pass presumably touched the pages that name keys, but nobody has confirmed the
*surrounding prose* still describes the behaviour, only the names.

## What a reader would do

A person who knows the behaviour reads each page top to bottom against the running application (or the code and the phase docs), and
corrects or annotates. Not something to delegate to a mechanical check; the value is exactly that a human notices "that is not how it
works any more". A good time is right after a release, with the Docs page open in the console so the rendering is checked too.

## Open questions

- Is there value in a `Last reviewed: <phase>` line at the top of each page, so staleness is visible to a reader as well as to a
  maintainer? It would need discipline to keep true, which is the same problem again.
