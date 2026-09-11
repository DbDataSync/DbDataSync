# Phase 126 — GitHub hosting moves to `github.com/DbDataSync/DbDataSync`

**Status**: Done.
**Plan reference**: `architecture/planning/done/nuget-org-publishing-and-github-hosting-move.md` §1.
Phase 92 (the code-level `DataSync` → `DbDataSync` rename) explicitly deferred this move as "a
separate operational decision"; this is that decision, made once a `DbDataSync` GitHub org existed to
receive it.

## What this built

- **A new repo, `DbDataSync/DbDataSync`**, created via `gh api orgs/DbDataSync/repos` — public,
  default branch `main`, description matching the CLI's own `<Description>`.
- **The full git history pushed there** (`git push neworigin main`), including a concurrent
  dev-versioning commit (`ae7e301`) that landed on the old repo mid-move — fetched and fast-forwarded
  into local `main` before pushing, confirmed by SHA that both remotes carry the identical commit.
- **The old repo (`danshryock/DataSync`) untouched** — private, as it was; no archive, no redirect
  notice, no deletion.
- **Both git remotes present in the working checkout during the transition** — `origin` (the old
  repo, left alone since another concurrent session may still push to it under that name) and
  `neworigin` (the new one). Promoting `neworigin` to `origin` is a follow-up once nothing else
  targets the old remote name.

## What this phase does not build

- Any change to `danshryock/DataSync`'s visibility, archival state, or content.
- A GitHub-side redirect or deprecation notice.
- Reserving `DbDataSync` as an organization-verified npm/PyPI/etc. name anywhere else — GitHub only.
- Repointing the local checkout's `origin` remote — see above.

## How it was verified

- `gh repo view DbDataSync/DbDataSync` confirms the repo exists, public, org-owned.
- `git ls-remote neworigin main` and `git ls-remote origin main` both report `ae7e3013b...`, matching
  local `HEAD` exactly — the new repo is not missing or ahead of anything the old one has.
- `gh api orgs/DbDataSync/memberships/danshryock` confirmed `admin` role on the org before creating
  the repo under it.

## Decisions

- **Create + push, not `repos/transfer`.** The old repo had nothing a transfer's redirect/history
  preservation would protect that an identical-history fresh push doesn't already carry (zero tags,
  releases, PRs, forks; one star, the owner's own). A transfer would also have renamed the repo in
  place rather than letting the new org repo start clean under its already-correct name.
- **Two remotes, not an `origin` repoint, during the move.** The working tree is shared with other
  concurrent sessions; changing what `origin` resolves to mid-session risks silently redirecting
  someone else's next `git push origin`. Additive (`neworigin`) avoided that risk entirely.
