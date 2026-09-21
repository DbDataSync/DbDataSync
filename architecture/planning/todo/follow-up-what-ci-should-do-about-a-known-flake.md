# What CI should do about a known flake, now that two more gates depend on a green run

**Status: open — a decision, not an implementation.** It is item 4 of
[the CI flake catalogue](follow-up-ci-is-red-on-most-pushes-from-unrelated-flaky-tests.md)'s own suggested
order of attack, which deliberately declined to make the call: *"Either is a policy choice; this doc doesn't
make it."* Nothing has made it since. This doc exists to make it decidable, and rules one of the three
options out on evidence the catalogue could not have had — the constraint below landed after it was written.

## Why it needs deciding rather than waiting

A red CI run on `dev` is not just noise. `promote-test.yml` fast-forwards `test` **only** on
`workflow_run.conclusion == 'success'`, so every red run is a missed promotion — and the catalogue recorded
the effect directly: after `b512cc5` went green on 2026-09-20, the next four `promote-test` runs were all
`skipped`, because every later commit's CI was red. A flaky suite silently converts "every green commit is
releasable" into "some commits get through."

## The constraint that removes one option

Two gates downstream of `test` now independently require a green CI run **for that exact SHA**, using the
same query:

- `release.yml` — "Require a green CI run for this exact commit" (added 2026-09-18).
- `publish-snapshot.yml` — the same check, so a snapshot is never cut from a commit that has no green run
  (phase 158).

```
gh api "repos/$REPO/actions/runs?head_sha=$SHA&status=success" --jq '[.workflow_runs[] | select(.name=="CI")] | length'
```

So **option (a) from the catalogue — let `promote-test` accept "only known-flaky jobs red" — is no longer
coherent.** It would advance `test` to a commit that has no successful CI run, and then both of those gates
would refuse it: unsnapshottable and unreleasable, promoted but stuck. Adopting (a) now means changing all
three in step and weakening the release gate specifically, which is the one place the project has been most
deliberate about not weakening. Not worth it to dodge a flake.

That leaves two.

## Option (b) — retry the flaky jobs once

Mechanically the cheapest, and it preserves the invariant the other gates rely on: a run that passes on its
second attempt **is** a successful run for that SHA, so `promote-test`, `publish-snapshot` and `release.yml`
all stay consistent with no changes. `release.yml`'s check already accepts any successful run for the SHA
rather than only the newest, precisely because "a flake that was re-run to green is the same situation a
human would have accepted by hand" — so a retry policy is the automation of what is already being done by
hand, several times a day.

What it costs is the thing worth weighing: **a retry hides a real intermittent product bug as effectively as
it hides a test-harness one.** Of the failures catalogued, at least two are product-adjacent rather than pure
test noise — the [`GetLogs` read-your-writes race](follow-up-getlogs-flush-does-not-guarantee-read-your-writes.md)
is a genuine defect that first appeared as a one-off flake, and the
[Event Log write denial](follow-up-event-log-tests-guard-registration-but-not-the-write.md) is unexplained.
Auto-retry would have quietly absorbed both. If retry is adopted it should be **visible** — the run's summary
should say a job needed a second attempt, so the frequency stays legible instead of becoming invisible.

## Option (c) — fix them, and stop needing a policy

The catalogue's list was long and partly undiagnosed when it was written. **It no longer is.** Every failure
it names now has a doc with a diagnosed cause and a fix shape:

| failure | doc | shape of fix |
| --- | --- | --- |
| SCD2 CDC identical mapped times | [phase-154 SCD2](follow-up-phase-154-scd2-cdc-timestamp-mapping-race.md) | test-harness; partly done (`a2a8d66` added a clock tick) |
| Playwright `ENOTEMPTY` teardown | [temp dir](follow-up-a-temp-dir-that-cannot-be-deleted-fails-a-job-whose-tests-all-passed.md) | don't fail a run on an undeletable temp dir |
| Bulk-load retrigger race | [phase-154 bulk load](follow-up-phase-154-bulk-load-race-retrigger-still-observational.md) | accept both outcomes, as the same test already does for `map-1` |
| `RunWatermarkTimeTests` wrong row | [scheduler](follow-up-runwatermarktimetests-claims-the-wrong-row-again-via-the-real-scheduler.md) | stop the test host scheduling; claim by run id |
| `MsSqlCdcReaderTests` floor | [CDC floor](follow-up-cdc-floor-test-asserts-a-row-count-its-own-guard-allows-to-be-wrong.md) | assert inclusivity, not a row count |
| Event Log write denial | [Event Log](follow-up-event-log-tests-guard-registration-but-not-the-write.md) | guard the write; skip unelevated |
| `UpdateConfirmationServiceTests` deadline | — | **done** (`c66b834`) |
| `RunnerStateEndpointTests` empty logs | [GetLogs](follow-up-getlogs-flush-does-not-guarantee-read-your-writes.md) | serialize `LogWriter.Flush` |

Most are small and test-side. Two (`GetLogs`, and whatever the Event Log write turns out to be) are product
changes worth making on their own merits.

## Recommendation

**(c), and treat (b) as a stopgap that has to be time-boxed if it is used at all.** The argument for a policy
was that the flake list was open-ended; it is now an enumerated list of eight, seven diagnosed and one done.
Spending the effort on a retry mechanism — plus the honest-list maintenance (a) would have needed — costs
about as much as the fixes and leaves the defects in place.

If CI's redness is intolerable before that work lands, adopt **(b) with visibility** and delete it when the
table above is empty. Do not adopt (a).

Worth noting for whoever picks this up: since the 2026-09-20 storm, `a2a8d66` and `c66b834` landed and
**every CI run on 2026-09-21 was green** — the pressure that made this urgent has eased, which is exactly
when a stopgap is most tempting to adopt permanently and least necessary.

## How to verify when closed

- A decision is recorded here, with its reasoning, and this doc moves to `planning/done/`.
- If (b): the retry is visible in the run summary, and this doc names the condition under which it is removed.
- If (c): the table above is empty, and several consecutive `dev` pushes go green with no re-runs — the
  catalogue's own definition of done.
