# Phase 16 — Replication Endpoints with Per-Mapping Override (planned)

**Status**: Planned, not started
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
