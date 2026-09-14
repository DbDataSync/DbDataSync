# Phase 135 — Windows service install takes ownership of the data directory

**Status**: Not started.
**Plan reference**: `architecture/planning/done/windows-service-data-directory-ownership.md`.

## Why

Root-caused a real production Error 1053 this session. `dbdatasync service install` registering
`LocalSystem` against a data directory that already existed (created earlier by an interactive
`dbdatasync setup` run) fails every start:

```
Could not prepare the config repository at 'C:\ProgramData\DbDataSync': repository path
'C:/ProgramData/DbDataSync' is not owned by current user
```

This is libgit2's ownership-safety check (the same protection as git's own CVE-2022-24765
`safe.directory` fix) — a check on the directory's **owner**, not its ACL. `icacls` showed
`NT AUTHORITY\SYSTEM:(F)` already present, inherited correctly from `%ProgramData%`'s default ACL, and
libgit2 refused anyway because ownership hadn't transferred.

`GrantDataDirectoryAccess` (`src/DbDataSync.Cli/ServiceCommand.cs`) grants ACL rights for a named
`--account` and explicitly skips the call for `LocalSystem`:

```csharp
if (account is null || string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
    return;
```

with the comment "LocalSystem needs no grant — its access already covers a directory anyone just
created." True for access rights, not for ownership — and a named account hits the identical ownership
check too, since the existing `/grant` call never transfers ownership either. Verified manually that
this resolves it:

```powershell
icacls "C:\ProgramData\DbDataSync" /setowner "SYSTEM" /T /C
```

The Linux path already gets this right — `SystemdService.cs:39` runs `chown -R` to the configured
`User=`/`Group=` unconditionally on every install. This phase brings Windows to parity.

## Design

Replace `GrantDataDirectoryAccess`'s early return with an unconditional two-step `icacls` run, for
every account including `LocalSystem` (icacls's recognized name for it is `SYSTEM`, not the string
`LocalSystem` the rest of this file uses — matching the manual verification above):

```csharp
private static void GrantDataDirectoryAccess(string root, string? account)
{
    Directory.CreateDirectory(root);
    var effectiveAccount = account ?? "SYSTEM";

    RunIcacls(root, ["/setowner", effectiveAccount, "/T", "/C"],
        $"take ownership of '{root}' for '{effectiveAccount}'");
    RunIcacls(root, ["/grant", $"{effectiveAccount}:(OI)(CI)M", "/T"],
        $"grant '{effectiveAccount}' access to '{root}'");
}
```

Two changes to the existing `/grant` step beyond just no-longer-skipping `LocalSystem`:

- **`/T` added** — the existing call never had it, so it only set *inheritable* rights for future
  children, never applied to files/subdirectories that already exist (the git repo internals, `state.db`)
  at the point `service install` runs against a pre-existing data directory — the exact scenario that
  triggered this bug in the first place.
- **Runs before `/grant`, not instead of it** — taking ownership alone gives the new owner implicit
  `WRITE_DAC` (the right to change permissions), not necessarily explicit data access; keeping the
  existing `/grant` step (now recursive) after `/setowner` is the same "own it, then also explicitly
  grant it" combination that resolved this manually.

`RunIcacls` factors out the existing `Process.Start("icacls")`/`WaitForExit`/warn-don't-fail pattern
`GrantDataDirectoryAccess` already has, so both steps share it rather than duplicating the
`ProcessStartInfo`/exit-code-warning boilerplate — same non-fatal posture as today: a failure here warns
and lets `service install` continue, it doesn't block registration.

## Checkpoints

1. `RunIcacls` helper extracted from the existing inline `icacls` `Process.Start` call in
   `GrantDataDirectoryAccess`, taking the argument list and a description for the warning message —
   no behavior change yet, just the extraction.
2. `GrantDataDirectoryAccess` rewritten per the design above: no more `LocalSystem` early return,
   `effectiveAccount` resolved once, `/setowner` then `/grant` (now with `/T`), both via `RunIcacls`.
3. Test coverage: `ServiceCommandTests` (or wherever `ServiceCommand`'s Windows-only pieces are already
   tested) gets a case proving `LocalSystem` now reaches the icacls calls instead of returning early —
   real filesystem `icacls` execution the way the fix will actually run, matching this repo's own
   "real, not mocked" precedent for install-time OS interaction, not a fake process runner.
4. Manual verification note in the retrospective: re-run the exact repro that surfaced this bug (a data
   directory created by an interactive `setup` run, then `service install --account` unspecified,
   i.e. `LocalSystem`) and confirm the service starts.

## Retrospective

Not yet implemented.
