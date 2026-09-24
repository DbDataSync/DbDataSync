# The bulk-load retrigger race is narrowed, not closed — and it is still asserted as if it were

**Status: open.** Found on 2026-09-17, on the phases-145/038/034/035 branch's first green-able CI run
after rebasing onto `main`. Filed per `architecture/implementation/README.md`'s "Follow-up work gets its
own doc, not a paragraph." Sibling of
`architecture/planning/todo/follow-up-phase-154-scd2-cdc-timestamp-mapping-race.md` — same phase, same
class of problem, different test.

## What is wrong

`BulkLoadIntegrationTests.ARaceBetweenAConcurrentReloadAndAMappingsOwnFirstPass_TheLoserFailsCleanly_AndSelfHeals`
failed in CI with:

```
Assert.Equal() Failure: Strings differ
Expected: "Failed"
Actual:   "Succeeded"
```

at the `map2Race` assertion. Nothing in the branch touches `BulkLoadIntegrationTests`, the bulk-load
path, or `ReadHold`; the same job passed on the re-run, and `main`'s own `dotnet-integration` was green
on the commit this branch rebased onto. It is the test, not the engine.

Phase 154 already fixed this test once — the fix replaced an immediate blind retrigger with a poll:

```csharp
var map2StillLoading = await PollUntilHoldAsync("map-2", ReadHold.Loading, TimeSpan.FromSeconds(5));
if (map2StillLoading)
{
    var raceRunIds = await ReadRunIdsAsync(await _client.PostAsync(...));
    ...
    Assert.Equal("Failed", map2Race.GetProperty("status").GetString());
```

and its own comment states the goal exactly right: *"Polling for the Hold narrows the window to a single
GET-then-POST instead of however long everything above happened to take."* **Narrows.** The window is
still there, and it is the GET-then-POST itself: `PollUntilHoldAsync` establishes that map-2 was
`Loading` at the moment it returned, and the code then asserts a property of the moment the POST is
*served*. Between the two, map-2's own Bulk Load — an independently-timed work item — can finish and
clear the hold, at which point the enqueued Primary pass has nothing to collide with, runs normally, and
succeeds. That is precisely the observed failure.

So the assertion is observational where it needs to be causal. A poll can prove a hold *was* set; it
cannot hold it there.

## Why it is a two-line fix, and what is worth noticing

The same test already solves this, ten lines above, for the structurally identical `map-1` case — it
checks both outcomes explicitly rather than assuming only the "lost" branch can happen, and says why in
a comment. The `map-2` branch should do the same: treat `"Succeeded"` as the equally-correct outcome
that means the window closed before the POST landed, exactly as the `else` branch below already does for
the "never observed Loading at all" case.

That costs nothing in coverage, and the test's own comment already says why:

> Covered deterministically instead by
> `RunExecutorTests.ExecuteWorkerAsync_AManualTriggerWhileStillLoading_FailsCleanly_BeforeAnyConnectionIsOpened`,
> which sets the hold directly rather than racing a real Bulk Load for it.

**The real property is already proven deterministically elsewhere.** What this integration test uniquely
contributes is that the wiring is real end to end — not that a particular branch of the race is taken.
Asserting the branch is asserting the runner's speed.

The thing worth noticing, and the reason this is a doc rather than a silent edit: this is the *second*
time this one test has been fixed for a timing assumption, and the first fix's own comment named the
remaining window while leaving an assertion that depends on it. A narrowed race in an integration test
reads as fixed and comes back later on a slower or faster runner. Where a deterministic unit test
already owns the property, the integration test should assert the shape both outcomes share, not pick
one.

## How to verify when closed

- The `map2StillLoading` branch accepts `"Succeeded"` as a legitimate outcome, with a comment saying
  which window closing it represents — matching the `map-1` branch above it.
- `RunExecutorTests.ExecuteWorkerAsync_AManualTriggerWhileStillLoading_FailsCleanly_BeforeAnyConnectionIsOpened`
  still passes, since it is what actually proves the behaviour.
- Nothing else in the test is relaxed: the `MappingStillLoading` failure kind and its message stay
  asserted on the branch where the run does fail.

## Two more occurrences, one on each side of the fix's own claim (2026-09-20)

`ARaceBetweenAConcurrentReloadAndAMappingsOwnFirstPass_TheLoserFailsCleanly_AndSelfHeals` failed twice more, and the
two failures are *opposite*:

- CI run `35495888790`, job `106038621029`, `BulkLoadIntegrationTests.cs:241`: `Expected: "Failed"`, `Actual: "Succeeded"`
  — the racing run that is supposed to lose won.
- CI run `35496100986`, job `106039212879`, `AssertAllSucceeded` at line 490 (called from line 258): the run
  that was supposed to succeed reported `Failed — Mapping 'map-1' … is still loading … This pass will retry automatically`.

Both commits were unrelated to this code (`5c7094a` changed docs only; `099230a` changed packaging and CI files).
Two outcomes in opposite directions from the same test is what "asserted as if it were closed while still
observational" predicts: the assertion picks one winner where the race can produce either.

## Applied (2026-09-22)

The `map2StillLoading` branch now checks both outcomes explicitly, matching the `map1Primary` branch ten lines
above it: `"Failed"`/`MappingStillLoading` is still asserted when the retrigger's POST is served while the hold
is still up, and `"Succeeded"` is now accepted as the equally-correct outcome when map-2's own Bulk Load clears
the hold in the GET-then-POST gap before the retrigger lands. Nothing else in the test was relaxed — the failing
branch's assertions (`MappingStillLoading`, "still loading") are untouched, and
`RunExecutorTests.ExecuteWorkerAsync_AManualTriggerWhileStillLoading_FailsCleanly_BeforeAnyConnectionIsOpened`
still exists and still owns the deterministic proof of the property. **Not proven:** this can only be confirmed
by CI not showing this test flip between "Expected: Failed / Actual: Succeeded" and green over several runs.

## Recurrence, 2026-09-23 (CI run `35915250548`, job `107364984345`)

The 2026-09-22 fix has not held. `ARaceBetweenAConcurrentReloadAndAMappingsOwnFirstPass_TheLoserFailsCleanly_AndSelfHeals`
failed again, on a docs-only push (no product code changed by that commit):

```
Expected every run to succeed, but got:
09664c27-9982-4a96-9f25-804ac52943e0: Failed — Mapping 'map-1' on 'bf-6ab30be25c1c4db08051ef4c7fc4cec9' is
still loading — an initial load is in flight and its watermark is not durable yet. This pass will retry
automatically once the load completes.
```

at `AssertAllSucceeded` (`BulkLoadIntegrationTests.cs:504`, called from line 272) — a different assertion
branch than the one the 2026-09-22 fix touched (`map2StillLoading`'s two-outcome check): this one still
asserts unconditional success and got the same "still loading" race outcome the fix already knows is a
legitimate, not-a-bug result elsewhere in the same test. Not re-diagnosed or re-fixed in this pass — logged
so it isn't lost, per this doc's own reason for existing. Still open; the race is real and evidently reaches
more than the one call site already patched.

## Diagnosed and applied (2026-09-24)

The gap: the test explicitly waits for map-2's real Bulk Load to clear `ReadHold.Loading`
(`WaitForLoadToCompleteAsync(_replicationName, "map-2")`) before firing the final "self-healing" retrigger
— but never did the same for map-1's own explicit reload, even though that reload is the one whose hold the
retrigger's own Primary pass depends on being clear. `MappingLoadWaiter.WaitForLoadToCompleteAsync`'s own
doc comment names exactly this shape of gap for the analogous Primary-pass case: a run going terminal
("Succeeded") is not the same moment as the hold it was holding actually clearing — there's a real
promotion step in between. Line 194 already asserts `bulkLoadRun`'s own status is `"Succeeded"`, but nothing
after that point ever confirmed map-1's `ReadHold` had actually cleared before the test fired a second,
whole-replication retrigger a few lines later. The 2026-09-23 recurrence's own failure text —
`Mapping 'map-1' ... is still loading — an initial load is in flight and its watermark is not durable
yet` — is exactly what firing that retrigger into the still-open gap produces.

**Applied**: added `await _client.WaitForLoadToCompleteAsync(_replicationName, "map-1");` immediately
before the existing map-2 wait, right before the final retrigger (`BulkLoadIntegrationTests.cs`, just above
the "Self-healing" comment). Safe in both branches of the earlier `map1Status` check — if map-1's own
Primary pass already succeeded (the `else` branch, no bulk load was ever pending), the wait returns
immediately since the hold was never set; if it failed with `ConcurrentLoadInProgress` (the intended
branch), this is exactly the wait that was missing.

**Verified**: built and ran `ARaceBetweenAConcurrentReloadAndAMappingsOwnFirstPass_TheLoserFailsCleanly_AndSelfHeals`
five times back to back against the real `dbdatasync-mssql-source`/`target` containers — all green. As with
every other fix in this doc and its sibling SCD2 CDC doc, this environment's own idle containers never
reproduced the original race even before the fix, so this proves no regression, not that the race is gone;
the real proof is whether "is still loading" recurs at this specific call site on CI.
