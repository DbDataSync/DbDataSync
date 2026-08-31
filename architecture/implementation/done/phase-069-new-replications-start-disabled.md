# Phase 69 — New replications start disabled

**Status**: Done.

## The change

A newly-created replication should start with `Enabled: false`, requiring an explicit opt-in to run,
rather than starting live the moment it's saved — a half-configured replication (no mappings yet, or
endpoints not yet pointed anywhere real) shouldn't be eligible for scheduling before an operator has
actually finished setting it up.

## Scope: the create flow only, not the config default

**`ReplicationsPage.tsx`'s "New replication" draft** (`create()`, around line 66) sends `enabled: true`
in the initial `ReplicationTaskConfig` it POSTs — changed to `enabled: false`.

**`ReplicationTaskConfig.Enabled`'s server-side default stays `true`, unchanged.** This is not the same
setting: the C# property's `[DefaultValue(true)]`/`= true` governs what an *existing* config file that
omits the `Enabled` key means on load (YAML serialization omits a field matching its default, so
omission has always meant "enabled"). Flipping that default would silently disable every already-saved
replication that has never explicitly written `Enabled: false` to its config — a real backward-
compatibility break, and not what was asked. The fix is scoped to the moment of *creation*, where the SPA
now explicitly sends `false` in the request body rather than relying on (or changing) what an absent
field would mean.

## How to verify

- Creating a new replication through the SPA and saving it without touching the Enabled toggle: the
  replication is disabled immediately after creation.
- An existing replication's config file, with no `Enabled` key present at all (relying on the old
  implicit-true-by-omission behavior), still loads as enabled — confirms the backend default is
  untouched and no existing config silently flips.
- The Enabled toggle in the replication header still works normally to turn a newly-created (disabled)
  replication on.
