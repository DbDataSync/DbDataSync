# Phase 94 — Auto-provisioning reports its columns back into the metadata cache

**Status**: Not started.
**Plan reference**: `architecture/planning/done/auto-provisioning-metadata-cache-update.md`

## The gap

`RunExecutor.EnsureTargetTableProvisionedAsync` (`RunExecutor.cs:832-897`) builds `provisioned` — the
exact column list it's about to create or alter — but never records it in `mapping.TargetColumns`
(phase 90's cache) afterward. A mapping auto-provisioning its target creates the table correctly, then
fails the same pass under phase 91's `MetadataNotCachedException` once a writer/staging provider needs
the cache, needing one manual Refresh before it can ever succeed.

## What to build

### New loopback capability, mirroring `SetWatermark`'s shape

TaskRunner has never written config before — every `SaveTableMapping` call today originates from an API
controller, matching phase 39's single-writer precedent for state. Extend the same loopback pattern
`SetWatermark` uses (`RunExecutor` → `IRunnerState`/sibling interface → `LocalRunnerState`/
`RemoteRunnerState` → protocol record → endpoint) so TaskRunner can report "this mapping's target
columns are now X" and have the API process perform the actual git-authored `SaveTableMapping`.

Resolve the plan doc's open question first: whether this belongs on `IRunnerState` itself or a sibling
interface — `IRunnerState` is explicitly about the state store, config is a different owned resource
(`ConfigRepository`, git-backed), so a sibling interface reusing the same loopback host/authentication is
the more consistent shape, but confirm the actual endpoint-hosting mechanics before committing to it in
code.

### In-memory update, same pass

Immediately after a successful `CREATE`/`ALTER` in `EnsureTargetTableProvisionedAsync`, set
`mapping.TargetColumns` on the in-memory object the method already holds — this fixes the *current*
pass without waiting on the loopback round-trip. The reported write (above) is what fixes every pass
after this one, reading a freshly-loaded mapping.

### Both paths (create and alter)

`CreateTargetTableIfMissing` and `AlterTargetTableColumns` both produce an authoritative `provisioned`
list once their DDL runs — wire the report from one shared point after either succeeds, not just the
create path.

## What this phase should not do

- Give `TaskRunner` direct git repository access — the whole point is reporting through the process that
  already owns config writes.
- Change `AlterTargetTableColumns`'s gating/behavior beyond adding the report.
- Handle concurrent-edit conflicts specially — whatever `SaveTableMapping`/git already does for a
  concurrent write applies unchanged.

## How to verify

- A test asserting a mapping with no existing target table, configured to auto-provision, succeeds on
  its first pass with no manual Refresh.
- A test asserting the mapping's config, re-loaded fresh (not the in-memory object the pass used),
  reflects the provisioned columns afterward — proving the report actually persisted, not just the
  in-memory fix.
- A test asserting `AlterTargetTableColumns` reports its resulting shape the same way create does.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.
