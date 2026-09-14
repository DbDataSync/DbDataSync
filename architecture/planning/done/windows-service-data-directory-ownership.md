# Windows service install doesn't take ownership of the data directory

**Resolved 2026-09-14.** Turned into `architecture/implementation/todo/phase-135-windows-service-data-directory-ownership.md`
— `GrantDataDirectoryAccess` takes ownership (`icacls /setowner`) for every account, `LocalSystem`
included, before its existing (now recursive) `/grant`.

Root-caused a real production report this session: installing the service to run as `LocalSystem`
against a data directory that already existed (created earlier by `dbdatasync setup` run interactively
by an admin) fails every start with Error 1053. The real error, only visible by redirecting the
process's own stdout/stderr manually (see the companion doc on startup diagnostics — this is exactly
the failure that doc exists to make visible without that workaround):

```
Could not prepare the config repository at 'C:\ProgramData\DbDataSync': repository path
'C:/ProgramData/DbDataSync' is not owned by current user
```

## Why

This is libgit2's ownership-safety check — the same protection git itself added for CVE-2022-24765
(`safe.directory`). It is a check on the directory's **owner** attribute, not its ACL. `icacls` on the
affected machine showed `NT AUTHORITY\SYSTEM:(F)` — full access rights, inherited correctly from
`%ProgramData%`'s default ACL — and libgit2 refused anyway, because the owner was still the admin
account that created the directory.

`ServiceCommand.Install`'s existing `GrantDataDirectoryAccess` (`src/DbDataSync.Cli/ServiceCommand.cs`)
grants ACL rights via `icacls <root> /grant <account>:(OI)(CI)M` for a named `--account`, and explicitly
skips the call entirely for `LocalSystem`:

```csharp
if (account is null || string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
    return;
```

with the comment "LocalSystem needs no grant — its access already covers a directory anyone just
created." That's true for access rights. It is not true for ownership, and this session proved it.

This is not LocalSystem-specific in principle: a **named** service account hits the identical ownership
check if the directory was created by someone else first — the existing `/grant` call adds access
rights, but never transfers ownership either. `GrantDataDirectoryAccess`'s whole approach needs an
ownership transfer, not just an ACL grant, for every account, not a LocalSystem-only branch.

## What already works, for comparison

The Linux path already does this correctly. `SystemdService.cs:39`:

```csharp
RunProcess("chown", ["-R", $"{user}:{group}", path]);
```

`chown -R` to the configured `User=`/`Group=` runs unconditionally as part of installing the systemd
unit. Windows needs the equivalent — verified manually that this resolves it:

```powershell
icacls "C:\ProgramData\DbDataSync" /setowner "SYSTEM" /T /C
```

## Proposed fix

Extend (or replace) `GrantDataDirectoryAccess` to take ownership of the resolved root for whichever
account the service will run as — `LocalSystem` included — the same way the Linux path already does,
unconditionally, on every `service install`. `icacls /setowner ... /T /C` is idempotent; running it
every install (not just the first) is simpler than trying to detect whether ownership already matches,
and matches the systemd path's own unconditional `chown -R`.

## Open questions

- Should this also run as part of `dbdatasync setup`'s own flow (not just `service install`), so a
  later `service install` run by a *different* admin than whoever ran `setup` doesn't hit the same
  problem from the other direction? Leaning toward: no — `service install` doing this unconditionally
  already covers that case regardless of who created the directory or when.
- Confirm `icacls /setowner` doesn't need anything beyond the rights `service install` already assumes
  (elevation, which the command already requires and detects via its existing "Access denied" ERROR_CONTROL
  handling in `Sc()`).
