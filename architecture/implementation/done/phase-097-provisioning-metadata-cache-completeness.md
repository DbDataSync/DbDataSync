# Phase 97 — Every path that creates or confirms a target's shape caches it

**Status**: Done.
**Plan reference**: `architecture/planning/done/apply-button-does-not-cache-provisioned-columns.md`

## The gap

Phase 94 made `RunExecutor.EnsureTargetTableProvisionedAsync`'s unattended provisioning path cache what
it creates (`CacheProvisionedTargetColumnsAsync`). The Setup card's **Apply** button runs the identical
DDL through the same planner (`ProvisioningService.ApplyAsync`) but never touches the cache — so a
mapping provisioned by hand (the deliberate, "preview the DDL first" flow) creates its target correctly
and then fails its first run under phase 91's `MetadataNotCachedException`, needing a Refresh anyway.
Separately, `uncachedButInShape`'s recovery (phase 94, `RunExecutor.cs:907`) only runs when
`mayCreate`/`mayAlter` is on — a mapping with both off (true of anything provisioned by hand, since
that's *why* it was provisioned by hand) can never reach it.

## What to build

### 1. Cache from `ApplyAsync`

After `ApplyAsync` successfully runs its DDL, read the target's live catalog back (same approach
`CacheProvisionedTargetColumnsAsync` already uses — reading the catalog rather than trusting the plan's
own column list, since `ProvisioningColumn` lacks native types/identity flags) and persist it into the
mapping's `TargetColumns` via the API's own `SaveTableMapping` (this runs in the API process already —
no loopback report needed, unlike phase 94's TaskRunner-side case).

### 2. Hoist `uncachedButInShape` above the `mayCreate`/`mayAlter` gate

Confirmed safe: `uncachedButInShape` only ever fires when `plan.State == ProvisioningState.Satisfied` —
no DDL involved — and reaching it requires `provisioner.PlanAsync(...)`, which is itself side-effect-free
(the same call the Setup card's preview already makes). Hoisting it changes nothing about what
"provisioning off" means; a run with both settings off still never issues `CREATE`/`ALTER`.

**Check `mapping.TargetColumns.Count == 0` before calling `PlanAsync` at all.** A mapping with
provisioning off and an already-populated cache should cost nothing extra per pass — only reach the
`PlanAsync` inspection when the cache is actually empty, which is the rare recovery case this exists for.

## What this phase should not do

- Change what `CreateTargetTableIfMissing`/`AlterTargetTableColumns` gate — no DDL runs differently;
  only the caching/recovery behavior around already-correct or newly-created tables changes.
- Add a live-fallback anywhere in the phase-91 read path itself — this phase is entirely about closing
  gaps in *when the cache gets populated*, not relaxing the cache-required design.

## How to verify

- A test asserting the golden-path scenario (`golden-path.spec.ts` test 18 — describe a mapping with no
  target table, save, Apply from the Setup card, trigger a pass) now succeeds without a manual Refresh.
- A test asserting a mapping with both provisioning settings off, pointed at a target table that already
  exists in the correct shape but has an empty cache, self-heals on its next pass.
- A test asserting that same mapping, once its cache is populated, makes no `PlanAsync`/live-catalog call
  on a subsequent pass — the steady-state cost stays zero.
- A test asserting `AlterTargetTableColumns`'s path (not just create) also caches correctly from
  `ApplyAsync`.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.

---

## Outcome

Built as specified, both halves. The hoist turned out to be four lines and one changed boolean rather
than a restructuring, and the `ApplyAsync` half needed no new plumbing at all — everything it wanted
was already registered. The one thing the doc did not anticipate is the third condition on `ddlRan`,
which is what actually keeps the hoist honest, and it is explained below.

The clearest evidence it works is `golden-path.spec.ts` test 18, which the plan doc opened by quoting
its failure. It passes, with no Refresh anywhere in it.

### What was built

**1. `ProvisioningService.CacheTargetColumnsAsync`** — after `ApplyAsync`'s DDL comes back
`Satisfied`, and only for the two target actions, it reads the target back and writes
`TargetColumns` + `ColumnsCapturedUtc`.

**2. The hoist, in `RunExecutor.EnsureTargetTableProvisionedAsync`** — `uncached` is now a third
reason to enter the method alongside `mayCreate`/`mayAlter`, tested before anything is planned;
`create` is planned when `mayCreate || uncached`; and `ddlRan` gained the permission term that makes
the widening safe.

**3. `CachedColumn.SameShape(a, b)`** — the list comparison three callers were each keeping a private
copy of (`MappingMetadataCapture.Same`, `LocalRunnerConfig.Matches`, and the one this phase needed).
Collapsed to one, because a save deciding "unchanged" on a rule a provisioning report disagrees with
would restamp a capture nobody took.

### Judgment call: the hoist is smaller than it looks, but it needs one more term

The doc asked whether hoisting meant restructuring the method's control flow. It does not — the
early return grows one disjunct and the create plan's condition grows one. But moving the gate exposes
something the original arrangement hid: with the gate above everything, *reaching* a plan was proof of
permission to run it, so `ddlRan = plan.State == Missing` was safe by position. Once `uncached` can
bring back a plan no setting authorised, that stops being true — a mapping with both settings off and
a missing target reaches a `Missing` create plan, and the unchanged line would have created the table.
That is exactly the semantics change the plan doc argued this phase does not make.

So authorisation is now asked of the plan in hand rather than inferred from position:

```csharp
var mayRunDdl = plan.Action == ProvisioningActions.CreateTargetTable ? mayCreate : mayAlter;
var ddlRan = plan.State == ProvisioningState.Missing && mayRunDdl;
```

This also fixes a latent case the old positional reasoning got away with: `mayAlter` on and
`mayCreate` off previously never planned a create at all, so it could not misfire; now that it plans
one, `mayRunDdl` is what stops a create plan running on alter permission.
`AMappingWithProvisioningOff_NeverCreatesTheTargetItInspectedFor` is the test that holds this line —
it asserts the pass fails and the table is *still not there* afterwards.

`mayRunDdl` also gates the `Unsupported` warning, which would otherwise tell a mapping that asked for
nothing that its target "cannot be auto-created".

### Judgment call: `ApplyAsync` needed no new plumbing, and read through the reader

The doc's second question, and the answer is nothing new: `ProvisioningService` already had
`ConfigRepository` and a `CurrentUser` (`MarkRenamesApplied` commits through both), and
`MappingColumnReader` was already a registered singleton. One constructor parameter.

Reading through `MappingColumnReader` rather than `IDriver.ListColumnsAsync` directly is a small
deviation from phase 94's shape, and deliberate. That class is already "the single introspection path
behind both Refresh and bulk create", and Apply is a third caller of the same question — so what Apply
caches is byte-for-byte what pressing Refresh would have cached. It also hands back the distinction
this needs for free: `SideRead.Shape` is null both for a target that could not be read and for one the
catalog says is not there, and neither is something to write into a cache phase 91 acts on.

**Attribution is the operator, not `SystemAuthor`.** Phase 94 reserved the system identity for "a
background service writing config on its own"; Apply is a person pressing a button, `MarkRenamesApplied`
already commits their name on this same path, and one Apply producing two commits under two identities
would be a worse record than either alone. `LocalRunnerConfig` was therefore reused for its *rule* —
re-load, apply one field, compare before committing — rather than called: it is bound to `SystemAuthor`
at composition, which is right for the runner and wrong here.

Ordering matters and is load-bearing: `MarkRenamesApplied` saves the whole in-memory mapping, so the
cache write runs *after* it and re-loads. Reversed, the rename save would revert the cache it just
wrote.

### Judgment call: compare before writing, and where that helper lives

The doc's third question. The check is simple enough to write inline, but it was already written
inline twice, so the answer was to stop writing it. `CachedColumn.SameShape` is the extraction; all
three callers now agree by construction rather than by comment. Without it, every Apply would restamp
`ColumnsCapturedUtc` and commit — and Apply on an already-correct target is a normal thing to press.
`Apply_OnATargetAlreadyInShape_DoesNotRestampTheCapture` is what keeps that true.

### Deviation: the recovery skips a source that names no table

Not in the doc, added on inspection. Planning needs the *source's* catalog, and a query-configured
source has none — `MappingColumnReader` states that condition in words already. Widening who reaches
`EnsureTargetTableProvisionedAsync` would have taken such a mapping to a catalog read on a blank table
name for the first time. Its outcome does not change either way (phase 91 refuses it on the empty
cache regardless); what the guard preserves is that it fails in phase 91's words, which name the
mapping and the action to take, rather than in a catalog error's.

### Deviation: the recovery says so in the run log

The pure-recovery case is the one time a pass reads catalogs nothing in its configuration asked it to
read, so it logs one line saying that and saying provisioning is still off. It happens once per
mapping — the next pass finds the cache filled and returns at the gate. It also made the "steady state
costs nothing" requirement testable: without an observable, "no `PlanAsync` call" and "a `PlanAsync`
call that decided nothing" are indistinguishable from outside, and asserting the absence of that line
is what distinguishes them.

### How it was verified

- `ApplyProvisioningCacheIntegrationTests` (new, 3, Integration): Apply creating a target caches the
  shape it created — compared against the target's own catalog, and asserting the *native* types
  (`int`, `nvarchar(50)`) that are the reason this reads back rather than trusting the plan; Apply
  altering a target caches the column it added; and a second Apply on an already-correct target
  commits nothing and leaves the capture timestamp alone. No `refresh-metadata` call appears in the
  file.
- `RunExecutorIntegrationTests` (+3, Integration): a mapping with both settings off whose target was
  created by hand *after* the mapping was saved recovers its empty cache on the next pass and
  replicates; the same mapping with a populated cache logs no inspection and does not restamp; and a
  mapping with both settings off and no target creates nothing.
- `golden-path.spec.ts` test 18, the scenario the plan doc opened with: passing. Full Playwright
  golden-path file **45 passed**. (One earlier run showed test 41 failing on `Login failed for user
  'sa'` inside a change poll; it passed on re-run and is unrelated — a flake under container
  contention.)
- Full suite: `Category!=Integration` **1237 passed, 3 failed, 32 skipped**; `Category=Integration`
  **235 passed, 0 failed** against the real containers. `tsc -b` and the SPA build clean; no SPA
  change was needed or made.

### Pre-existing failures, confirmed as such

Three, all named in phase 94's own retrospective as pre-existing and none in a file this phase
touched: `InviteCommandTests.Invite_AgainstAnMsSqlConfiguredRepo_Succeeds`,
`SecretCommandTests.Set_IsResolvableUnderTheDbDataSyncPrefix_ButNotUnderThePackagesUnconfiguredDefault`,
and `ChangePollingGateTests.CdcAndChangeTrackingInOneDatabase_AreTwoGroupsWithTwoAuditRows`. The two
`AdminConfigControllerTests` file-source tests phase 94 also listed now pass.

### What this phase did not do

No live fallback was added to the phase-91 read path: a writer still reads the cache and nowhere else,
and still throws when it is empty. What changed is only how many ways the cache gets filled — now
three, and between them they cover any target that is fine but was never cached, whatever the cause.
`CreateTargetTableIfMissing` and `AlterTargetTableColumns` gate exactly the DDL they always gated.
