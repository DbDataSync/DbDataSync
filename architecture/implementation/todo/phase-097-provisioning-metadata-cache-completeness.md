# Phase 97 — Every path that creates or confirms a target's shape caches it

**Status**: Not started.
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
