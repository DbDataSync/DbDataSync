# A target created by the Apply button never gets its shape cached, so the mapping fails its first run

**Status: resolved 2026-09-03 — both fixes, see below. Reproduces on `main`.**

`golden-path.spec.ts` test 18 fails:

```
Failed: Table mapping 'orders' has no cached target column metadata. This mapping's target shape
has never been captured, or a save cleared it. Use Refresh metadata on the mapping to populate it
before this run can proceed.
```

Confirmed pre-existing and not phase 96's, by running the same file at `c8c41e8` — the commit before
it — and getting the identical failure.

## What the test does, and why it is a real scenario

Describe a mapping whose target table does not exist, save it, apply the target plan from the Setup
card, then trigger a pass. That is the ordinary deliberate flow: an operator who wants to see the DDL
before it runs uses Apply rather than switching on unattended provisioning.

## Two paths create a target table, and only one of them caches its shape

Phase 91 made readers and writers run from `TableMappingConfig.TargetColumns` and nowhere else, and
throw when it is empty. Phase 94 fixed that for the *unattended* path — a run that provisions its own
target now records what it provisioned. It did not fix the manual one.

- **Unattended.** `RunExecutor.EnsureTargetTableProvisionedAsync` → `CacheProvisionedTargetColumnsAsync`.
  Works, and phase 94 even covers the second-order case: `uncachedButInShape` records the shape of a
  target that is *already* in shape but has never been cached.
- **Apply button.** `ProvisioningService.ApplyAsync` runs the same DDL through the same planner and
  then writes config only in `MarkRenamesApplied`. `TargetColumns` is never touched.

And the recovery cannot save it, because of where the early return sits:

```csharp
var mayCreate = ProvisioningResolution.CreateTargetTableIfMissing(task, mapping);
var mayAlter  = ProvisioningResolution.AlterTargetTableColumns(task, mapping);
if ((!mayCreate && !mayAlter) || targetDriver is not IProvisioner provisioner)
    return;                                    // ← above the uncachedButInShape branch
```

A mapping provisioned by hand has both settings off — that is *why* it was provisioned by hand — so the
run returns before phase 94's recovery is reachable. The operator's only way out is Refresh metadata,
which is exactly the manual step phase 94 set out to remove.

## Which of the two to fix is the actual question

Both are defensible and they are not the same fix:

1. **Cache from `ApplyAsync`.** The narrow fix, and it matches phase 94's own principle — whoever
   creates the table records what it created. It means the API's provisioning path gaining a
   `SaveTableMapping` for the column cache, reading the catalog back rather than trusting the plan, for
   the reasons `CacheProvisionedTargetColumnsAsync`'s doc comment already sets out.
2. **Move the early return.** Let a run cache the shape of a target that is in shape even when
   provisioning is off, by hoisting `uncachedButInShape` above the `mayCreate/mayAlter` gate. Wider,
   and it makes the *first pass* self-heal for any mapping in this state however it got there — but it
   means a run planning provisioning for a mapping that has provisioning switched off, which is a
   change to what that setting means and needs thinking about.

## Resolved: both, and (2) is not the semantics change it looked like

**(1)**, confirmed as the smaller, correct fix: cache from `ApplyAsync` directly, reading the target's
live catalog back (matching `CacheProvisionedTargetColumnsAsync`'s existing reasoning) rather than
trusting the plan's own column list.

**(2)**, re-examined and confirmed *not* to change what "provisioning off" means. `uncachedButInShape`
(`RunExecutor.cs:907`) only ever fires when `plan.State == ProvisioningState.Satisfied` — the provisioner
has already determined the table exists and needs no DDL. Reaching that check requires calling
`provisioner.PlanAsync(...)`, which is itself side-effect-free — the same call the Setup card's preview
already makes before an operator ever clicks Apply. So hoisting `uncachedButInShape` above the
`mayCreate`/`mayAlter` gate never provisions anything for a mapping with both settings off; it only
*inspects* a table already confirmed to need no change, and caches what it finds. No DDL runs either
way — "provisioning off" still means exactly what it always meant.

**One refinement, to keep this free for the steady state**: check `mapping.TargetColumns.Count == 0`
*before* calling `PlanAsync` at all. A mapping with provisioning off and an already-populated cache
(the common case, once caught up) should pay zero extra cost per pass — only a mapping with provisioning
off *and* an empty cache needs the inspection call, which is exactly the rare recovery scenario this
exists for.

Both together close the whole class this doc originally scoped only the Apply button out of: any target
that's fine but was never cached — a mapping restored from older config, one whose cache a save cleared,
or one provisioned by any future third path — self-heals on its next pass, regardless of cause.

## Worth noting about the test

Test 18 has been failing since phase 94 landed and nobody noticed, because the Playwright suite is not
in CI — `.github/workflows/ci.yml` runs `npm run build` and no E2E. Addressed separately, in
`architecture/planning/done/playwright-suite-in-ci.md`.

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-097-provisioning-metadata-cache-completeness.md`.
