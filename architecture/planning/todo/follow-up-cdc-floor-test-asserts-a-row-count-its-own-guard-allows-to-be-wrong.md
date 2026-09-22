# The CDC floor test asserts a row count that its own guard explicitly allows to be wrong

**Status: open.** Recorded as a row in
[the CI flake catalogue](follow-up-ci-is-red-on-most-pushes-from-unrelated-flaky-tests.md) (run
`35497291033`, 2026-09-20) and marked "**new** … not investigated"; this is that investigation.

## The symptom

`dotnet-integration` → `MsSqlCdcReaderTests.ChangesFromEarliest_ReturnsTheChangeAtTheFloor_InclusiveOfMinLsn`:

```
Assert.Single() Failure: The collection contained 2 items
```

The catalogue guessed it belonged to the same family as the SCD2 timestamp failures. It does not — the
cause is local to this test, and the test already documents it.

## The test contradicts itself, and says so in a comment

It inserts two rows, then prunes the change table below the second row's LSN so only the second change
survives, then asserts one row comes back. Between those steps it guards the pruning — and the comment on
that guard is the whole bug:

> The feed's floor moved forward but not to an exact value: `sp_cdc_cleanup_change_table` rounds
> `start_lsn` down to the nearest `cdc.lsn_time_mapping` entry, **so under load it can land just shy of
> the low-water mark** rather than on it. What is guaranteed — and what the reads below rely on — is that
> it advanced past the empty starting floor and did not overshoot the mark.

```csharp
Assert.True(Compare(minLsn!, new byte[10]) > 0, "the floor did not advance");
Assert.True(Compare(minLsn!, FromWatermark(afterSecond)) <= 0, "the floor overshot the low-water mark");
...
var row = Assert.Single(earliestRows);
Assert.Equal(2, (int)row["Id"]!);
```

The guard permits the floor to land anywhere in `(0, afterSecond]`. **"Just shy of the low-water mark" is
inside that range and is exactly the case where the first row's change is not pruned** — so
`ChangesFromEarliest` correctly returns both rows, and `Assert.Single` fails. The guard's own tolerance and
the assertion's precision cannot both be right. Under load, which is what a busy CI runner is, the rounding
lands short often enough to be seen.

Note the assertions that failed are not detecting a product bug: returning two rows when the floor sits
below both of them is the reader behaving correctly.

## What the test is actually for

Its name states the property: **`ChangesFromEarliest` returns the change *at* the floor, inclusive of
`minLsn`.** That is a claim about inclusivity — that the row sitting exactly on `minLsn` is not skipped, the
bug the rejected `sys.fn_cdc_increment_lsn` design would reintroduce (which the second half of the test
demonstrates, and which is unaffected by any of this).

"Exactly one row comes back" is not that property. It is a proxy that happens to hold when the prune lands
precisely, and the test bought precision it was told it would not get.

## Fix shape

Assert the property, not the proxy — derive the expectation from the floor that was actually observed
rather than the one that was requested:

- **Assert the first returned row's `__$start_lsn` equals `minLsn`.** That is inclusivity, stated directly,
  and it holds whether the prune landed on the mark or short of it.
- Or keep a count assertion but compute it from `minLsn`: if the floor is at or below the first row's LSN,
  two rows are correct; otherwise one. This keeps the coverage and stops asserting the runner's timing.

Either way, keep the guard comment — it is right, and it is what made this diagnosable. What should change
is the assertion it sits above.

Avoid "retry until the prune lands exactly": that trades a wrong assertion for a slow one, and the precise
landing is not something the product promises.

## How to verify when closed

- The test passes when `sp_cdc_cleanup_change_table` lands short of the low-water mark — forced directly by
  pruning to a mark between the two rows' LSNs, rather than waiting for a loaded runner to produce it.
- The inclusive-read property is still asserted, and the rejected-design half of the test (`ReadIntent.Changes`
  from the same LSN returning nothing) is untouched.

## Applied (2026-09-22)

Took the second fix shape: the row count is now derived from the floor actually observed
(`MsSqlCdcCatalog.Compare(minLsn!, MsSqlCdcCatalog.FromWatermark(afterFirst)) <= 0` decides whether the first
row's change survived the prune, so 2 vs. 1 is computed rather than hard-coded), and the second row is asserted
by taking the *last* returned row rather than assuming there is exactly one. The guard comment above it (the one
that made this diagnosable) is untouched, and the rejected-design half of the test is untouched. Not yet forced
directly (pruning to a mark between the two rows' LSNs on purpose) — the derived assertion is correct either way
the real prune lands, which is what matters, but deliberately reproducing the short-landing case would be a
stronger regression guard if anyone wants to add it later.
