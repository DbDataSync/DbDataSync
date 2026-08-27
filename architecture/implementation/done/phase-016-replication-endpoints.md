# Phase 16 — Replication Endpoints with Per-Mapping Override

**Status**: Built
**Plan reference**: `DataSync Mockups.dc.html` (the Claude Design project behind phase 15) and the
review that followed it. Phase 15 omitted the design's `Endpoints` card from the replication Overview
on the grounds that source and target belong to a *table mapping*, not to the replication. **That was
the wrong call** — the design is right, and this phase brings the model to it.

## What this phase will build

A replication owns its endpoints; a table mapping inherits them and may override them.

**Config model.** `ReplicationTaskConfig` gains endpoints:

```csharp
public sealed class EndpointRef       { public required string ConnectionName; public required string Database; }
public sealed class TaskEndpoints     { public EndpointRef? Source; public EndpointRef? Target; }
// ReplicationTaskConfig gains: public TaskEndpoints Endpoints { get; set; } = new();
```

and a table mapping's table refs carry an **optional** endpoint:

```csharp
// today:  TableRef { ConnectionName, Database, Schema, Table }
// after:  TableRef { EndpointRef? Endpoint, Schema, Table }   — null Endpoint means "inherit"
```

**No migration, by construction.** Existing config has a full connection/database on every mapping and
no endpoints on the task, which reads back as *every mapping overrides* — exactly what those configs
mean today. Nothing has to be rewritten on disk, and a replication can be tidied up later by lifting
the common endpoint to the task, which is a normal edit rather than an upgrade step.

**Resolution, in one place.** A `ResolveEndpoints` helper merges task endpoint + mapping override +
schema/table into the fully-resolved refs the drivers already consume. **The driver contract does not
change** — `IChangeReader`, `IStagingProvider` and `IChangeWriter` keep taking a `TableRef` with a
connection and database on it. That matters: phases 17–20 are building against those interfaces, and
this phase must not move them.

Two call sites resolve today and will call the helper instead: `RunExecutor.RunMappingAsync` and
`BackfillService.ExpandAsync`.

**Validation.** A mapping with no endpoint under a task with none either is a configuration error, and
must be rejected at save with a message naming which side is unresolved — not discovered when a run
fails to open a connection.

**SPA.**
- **Overview** gains the `Endpoints` card the design shows and phase 15 left out: source and target
  connection + database, with the note that they apply to every table mapping.
- **Mapping editor** gains the design's `INHERITED` badge and its **Override for this table** toggle.
  Off: the inherited endpoint is shown read-only, exactly as the mockup draws it. On: the connection
  and database pickers appear and the mapping carries its own.
- The mapping's schema/table pickers cascade from the *resolved* endpoint either way, so choosing a
  table works identically whether the endpoint is inherited or overridden.

## What this phase does not build

Anything about drivers — this is a config-model and UI phase and touches no engine. Multiple sources
or targets per mapping (the lists stay 1:1 in v1, as `RunExecutor` already enforces). Any change to
the credential model.

## How to verify when built

- Round-trip tests in `DataSync.Core.Tests`: a task with endpoints and an inheriting mapping; a
  mapping that overrides; and — the compatibility case — a config written in today's shape reading
  back as an overriding mapping with identical resolved values.
- Resolution unit tests: inherit, override, partial override (source inherited, target overridden),
  and the unresolved-endpoint error naming the offending side.
- `Category=Integration`: a replication whose mappings inherit runs end to end; a replication with one
  inheriting and one overriding mapping runs both correctly in the same pass — which is the case the
  whole feature exists for.
- Playwright: the Overview endpoints card, and the mapping editor's override toggle switching between
  inherited-read-only and editable.

## Open questions

- Whether the SPA should offer to *lift* a common endpoint to the task when every mapping has the same
  one. Useful, and obvious once the model exists — but it is a migration affordance, not the feature,
  and is easy to add once the shape has settled.
- Whether an override should be allowed to change only the database, keeping the task's connection.
  The model above allows it (an `EndpointRef` is both fields), but the design's toggle implies
  overriding both together. Worth deciding when the toggle is built rather than in advance.

---

# Retrospective

Built as planned, with one deliberate deviation to the config shape and one open question answered by
building it.

## The deviation: two nullable fields, not a nullable `EndpointRef`

The plan had `TableRef { EndpointRef? Endpoint, Schema, Table }` — one nullable object meaning
"inherit". What shipped keeps the fields flat and makes each one nullable:

```csharp
public class TableSpec { public string? ConnectionName; public string? Database; public string Schema = "dbo"; public required string Table; }
```

The reason is the plan's own no-migration claim. A nullable nested `Endpoint` changes the YAML shape:
`connectionName:` at the mapping's top level would have had to move under an `endpoint:` key, so every
existing config *would* need rewriting — the opposite of what the plan promised. Flat-and-nullable
reads existing config back unchanged: both fields present simply means "this mapping overrides", which
is what those configs already mean.

The plan's `TaskEndpoints`/`EndpointRef` for the *task* side survived intact — nesting is right there,
because the task's endpoints are a new key with nothing to be compatible with.

A `TableSpec` (what config holds, endpoint optional) is now a distinct type from a `TableRef` (what a
driver receives, everything resolved and non-null), with `SourceTableSpec`/`SourceTableRef` adding the
filter. The split is worth the two extra types: a driver cannot be handed an unresolved ref, because
the type it takes has no nullable fields to leave unset.

## The open question, answered: fields resolve independently

The plan asked whether an override should be allowed to change only the database. It is:
`EndpointResolution` falls back per field, so a mapping can override the database while keeping the
task's connection. Building it settled it — the archive-table case (same server, different database)
is common enough that forcing both fields to be restated would make the override toggle a chore, and
per-field fallback costs nothing to implement.

The UI keeps the design's single toggle: it flips both fields at once, seeded from what is currently
in effect, so overriding one field means turning the toggle on and changing one picker. The model is
more permissive than the toggle; the toggle stays as drawn.

## Saving a mapping now requires its replication to exist

Validation has to resolve against the task, so `SaveTableMapping` loads it. That is a new failure mode
for a call that used to be independent, and it surfaced immediately as two `ConfigRepositoryTests`
failing with `FileNotFoundException`. The tests were wrong, not the code — a mapping under a
replication that does not exist was always meaningless — so they now create the task first, and
`TableMappingsController.Upsert` translates the exception into a 404 naming the replication rather
than letting it escape as a 500.

## The golden path was restructured, not just extended

Playwright test 04 used to pick a connection, database and table on both sides of a mapping. That is
now the *override* path, not the normal one, so the suite would have gone on exercising a corner case
as if it were the main flow. It was split: a new test 04 sets the replication's endpoints on Overview,
test 05 adds a mapping that inherits them and asserts the connection picker is **not** rendered, and a
new test 12 drives the override toggle on, saves, and turns it back off — the round trip that proves
an override is not a one-way door.

## Verification

- `EndpointResolutionTests` — 9 tests: inherit, override, per-field fallback, source and target
  resolving separately, the filter surviving resolution, both error messages naming the side and
  field, `ConfigWrittenBeforeEndpointsExisted_ResolvesUnchanged`, and `Validate` checking every side
  of every mapping.
- `TableMappingsControllerTests` — inheriting and overriding mappings coexisting in one replication
  (asserting the saved config stays *sparse*, so changing the task's endpoint moves every inheriting
  mapping), the unresolved-endpoint 400, and the unknown-replication 404.
- Full .NET suite green: 213 tests across the six projects.
- Playwright: 12 tests green against a live SQL Server, including the two new ones.
- `npx tsc -b` clean; `oxlint` back to its three pre-existing `set-state-in-effect` warnings after
  `resolveSide` moved out of `MappingSide.tsx` into `api/resolveEndpoint.ts` (a component file that
  also exports a helper trips `only-export-components`).

## Still not built

The lift-a-common-endpoint-to-the-task affordance from the open questions. The model now makes it a
pure UI convenience, so it stays deferred.
