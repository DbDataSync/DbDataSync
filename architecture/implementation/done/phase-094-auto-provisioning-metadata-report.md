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

---

## Outcome

Built as specified, with one substantive deviation: what gets reported is the target catalog's answer
rather than the plan's `provisioned` list, and the report fires on one more condition than "DDL just
ran". Both are explained below, and the second is what let the whole journal question be answered with
"no journal".

The clearest evidence it works is a deletion. `Scd2NaturalKeyIntegrationTests` had a
`ProvisionTargetAsync` helper — thirty lines reproducing the production provisioner — added by phase 91
because a mapping whose target did not exist until mid-pass could not survive the first pass. That
helper is gone, and its three tests pass with the run creating both history targets for itself.

### What was built

Six points, mirroring `SetWatermark`'s chain:

- `IRunnerConfig` (`DbDataSync.Core/Config/IRunnerConfig.cs`), one method:
  `ReportProvisionedTargetColumns(replicationName, mappingName, columns)`.
- `LocalRunnerConfig`, beside it — the owner's implementation, over `ConfigRepository` + `GitAuthor`.
- `ReportProvisionedTargetColumnsRequest` in `StateProtocol`.
- `RemoteRunnerConfig` in `DbDataSync.State.Remote`, on the same `HttpClient` `RemoteRunnerState` uses.
- `RunnerConfigEndpoints`, mapped by `StateHost` beside `RunnerStateEndpoints`.
- `RunExecutor.CacheProvisionedTargetColumnsAsync`, called from one shared point after either DDL path.

`EnsureTargetTableProvisionedAsync` now sets `mapping.TargetColumns` on the object it was handed before
it reports, so the current pass never waits on — or depends on — the round trip.

### Judgment call: a sibling interface, on the same channel

The plan doc's open question, resolved the way it leaned. `IRunnerState`'s own doc comment says it is
"every state operation a TaskRunner performs", its split into prerequisites and outcomes is what makes
the offline journal expressible at all, and every member lands in a SQLite table. Config is a
different owned resource: `ConfigRepository`, git-backed, validating, one commit per write. Putting a
git commit behind a name that says "state" would read correctly once.

But the *transport* is shared, deliberately, and one detail decided it: `RunnerStateGuard` matches on
`StateProtocol.Route`. A route group with a prefix of its own would have been anonymous, on a loopback
listener, with no token check — the exact hole the guard exists to close, one refactor away. So
`/report-provisioned-target-columns` lives under `/internal/state` despite the name, and
`RunnerConfigEndpointTests` asserts the 401 that proves it. Two interfaces, one connection: what is
being written is a different question from how a child reaches its parent.

### Judgment call: no journal, and what earns that

`SetWatermark`'s journal exists because its value can never be recomputed — the rows are written, and
nothing will ever produce that position again. A provisioning report is not like that, but only
because of how the trigger condition is written. Reported strictly on "DDL just ran", a lost report
*would* be unrecoverable: the next pass finds the table already there, plans `Satisfied`, reports
nothing, and fails on the empty cache forever — needing exactly the manual Refresh this phase exists to
remove.

So the condition is broader. `RunExecutor` reports when DDL ran **or** when the plan came back
`Satisfied` and `mapping.TargetColumns` is empty — a target already in shape whose shape has never
been cached. That makes the report recur, which makes losing one cost one pass rather than an
operator's attention, which is what makes best-effort delivery honest rather than convenient.
`RemoteRunnerConfig` therefore has no journal and no grace period: it logs precisely what was not
delivered and returns, and it steps over a 4xx for the same reason — failing a pass whose table was
created and whose rows are ready, over a cache entry the next pass offers again, would turn a reporting
bug into a replication outage.

The cost of a recurring report is commit churn, and `LocalRunnerConfig` is where that is paid off: it
re-loads the mapping, compares, and returns without committing when the cache already agrees.
`A_repeated_report_of_the_same_shape_commits_nothing` is the test that keeps the two halves honest —
the recurrence is free, so it does not have to be rationed.

### Deviation: the catalog's answer, not the plan's

The doc says to record `provisioned`. `CacheProvisionedTargetColumnsAsync` re-reads the target through
`IDriver.ListColumnsAsync` instead, for three reasons that are all in the types:

- `ProvisioningColumn` carries a `CanonicalType`, not the native type string the target rendered it to
  — and that string is what staging builds its own DDL from.
- `ProvisioningColumn` has no identity flag. `CachedColumn` does, and a writer decides
  `IDENTITY_INSERT` on it.
- On the alter path `provisioned` names only the mapped-plus-historized columns, so a table with
  unmapped columns of its own would be cached *narrower than it is*.

Reading the catalog is also exactly what Refresh metadata does, so the cache has one answer in it
rather than two that can disagree — `TheProvisionedShape_IsOnDiskForTheNextPassToRead` asserts the
stored columns equal what the driver reports for that table. This is a live introspection inside a run,
which phase 91 otherwise has none of; it is provisioning's existing exemption, not a new one — the same
method already reads the *source* catalog on every pass it runs, because a plan cannot be made without
looking.

### Judgment call: `SystemAuthor`, already reserved

No new identity was invented. `CurrentUser.SystemAuthor` (`DbDataSync <dbdatasync@localhost>`) exists,
and `CurrentUser.Author`'s doc comment already reserves it for "a background service writing config on
its own" — a case that had never occurred until now. `LocalRunnerConfig` takes a `GitAuthor` rather
than reaching for it, because `DbDataSync.Core` cannot reference the API's auth types; the composition
root in `DbDataSyncHost` supplies it. Attributing the commit to whoever last signed in would have put a
person's name on a write they did not make.

### How it was verified

- `RunnerConfigReportTests` (new, 5): the owner's implementation against real config and real git — a
  report landing on disk with `ColumnsCapturedUtc` stamped, the commit carrying the identity it was
  given, an edit made mid-pass **not** reverted (the report applies one field to the current file, not
  a whole mapping as old as the pass), a repeated identical report committing nothing and not
  restamping, and a changed shape overwriting.
- `RunnerConfigEndpointTests` (new, 5): the same over the real Kestrel socket — a `RemoteRunnerConfig`
  report becoming a committed change with the runner never touching the repository, the token check
  covering the config route in both its wrong-token forms, a report naming no columns refused (an empty
  cache is a state phase 91 acts on; accepting a bad read would clear a good picture), and an
  unreachable owner producing one message and no throw.
- `RunExecutorIntegrationTests` (+3, Integration): the first pass of a mapping that provisions its own
  target succeeding with nothing pressed, the provisioned shape being on disk for the next pass —
  followed by a real second pass reading it — and the alter path reporting `[Id, Name]` after adding a
  column to a table whose cache correctly said `[Id]`.
- `Scd2NaturalKeyIntegrationTests` (3, unchanged assertions): its pre-provisioning fixture deleted, so
  the run creates both SCD2 history targets itself. This is the phase-91 workaround being paid back.
- Full suite: `Category!=Integration` **1207 passed, 5 failed, 32 skipped**; `Category=Integration`
  **228 passed, 0 failed** against the real containers. `tsc -b` and the SPA build clean; no SPA change
  was needed or made.

### Pre-existing failures, confirmed as such

Five, all confirmed by running the same two projects on stashed clean `main`:
`InviteCommandTests.Invite_AgainstAnMsSqlConfiguredRepo_Succeeds`,
`SecretCommandTests.Set_IsResolvableUnderTheDbDataSyncPrefix_ButNotUnderThePackagesUnconfiguredDefault`,
the two `AdminConfigControllerTests` file-source tests, and
`ChangePollingGateTests.CdcAndChangeTrackingInOneDatabase_AreTwoGroupsWithTwoAuditRows`. None is in a
file this phase touched. (The Windows-only tests earlier phases counted here now skip rather than fail,
per commit `2325cec`.)

### What this phase did not do

`TaskRunner` still has no git access and no `ConfigRepository` write of its own — the report is the only
config write a run can make, and the API performs it. `AlterTargetTableColumns`'s gating is unchanged:
the same two resolved settings decide the same DDL, and the only thing added after them is the report.
No concurrent-edit reconciliation was invented; a racing operator edit is handled by the one property
`LocalRunnerConfig` actually needs — that it writes one field of the file as it stands now — and
whatever `SaveTableMapping` and git already do applies unchanged.
