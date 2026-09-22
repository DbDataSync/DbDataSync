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
