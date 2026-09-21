# `dbdatasync`'s own help says "see docs/install.md", a path that does not exist where the tool is installed

**Found** 2026-09-20, phase 162's audit. Not changed there: it is help text, not docs.

`src/DbDataSync.Cli/Help.cs` points a reader at `docs/install.md` in two places:

- line ~24: `--self-update` … "(see docs/install.md, "From the web console")"
- line ~30: `tool install` … "see docs/install.md for the full sequence"

On a machine that has only the tool installed there is no `docs/` folder relative to anything the reader is standing in. Since phase 160
the same page is available three ways that *do* work there — **Docs → Install** in the web console, the same file in the repository, and the
rendered copy on GitHub — but the help names none of them. Similar text may exist in error messages elsewhere (`grep -rn "docs/" src
--include=*.cs`); only `Help.cs` was checked.

## Options

- **A.** Say what to open: "see the Install page in the console's Docs (or docs/install.md in the repository)". Costs nothing; still not a
  link a terminal can follow.
- **B.** Give a URL pinned to the running version — `https://github.com/DbDataSync/DbDataSync/blob/<commit>/docs/install.md`, using the
  informational version's `+<commit>` suffix (which a release build carries). That is the same construction phase 162 uses for the NuGet
  README, and points at exactly the docs for the version being run. Falls back to A for a build with no commit.
- Whichever is chosen, a test that no help or error string in the CLI names a repo-relative path would keep it from returning.

## Open question

- Is a printed URL acceptable for the offline/air-gapped case this project cares about elsewhere? The console's Docs page covers that; the
  help is where someone is *before* the console is running, so A's "open the console's Docs" is only useful after `serve` starts.
