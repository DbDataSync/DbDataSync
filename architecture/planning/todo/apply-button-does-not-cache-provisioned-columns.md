# A target created by the Apply button never gets its shape cached, so the mapping fails its first run

**Status: found 2026-09-03, diagnosed, not yet agreed. Reproduces on `main`.**

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

(1) is the smaller claim and probably right on its own. (2) is worth considering *as well*, because the
class of "a target that is fine but was never cached" is bigger than the Apply button — a mapping
restored from an older config, or one whose cache a save cleared, lands in it too.

## Worth noting about the test

Test 18 has been failing since phase 94 landed and nobody noticed, because the Playwright suite is not
in CI — `.github/workflows/ci.yml` runs `npm run build` and no E2E. That is its own thought and does
not belong in this file, but it is why a shipped defect in the flow this test covers went unseen.

**Next step**: decide between (1), (2) or both, then a phase doc.
