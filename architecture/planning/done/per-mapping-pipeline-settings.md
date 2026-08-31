# Per-mapping pipeline settings, starting with the SCD2 natural key

**Resolved 2026-08-31.** The SCD2 writer's `naturalKey` was a replication-wide `WriterConfig` option,
only correct if every table in a replication shared the same natural-key columns — true approximately
never. Agreed: reader/cache/writer Kind and options become independently overridable per table mapping,
and the natural key defaults itself from the source's primary key (primary-key only for now; no
unique-constraint support yet) rather than being a required, hand-typed, replication-wide value. Migration
of any existing replication-level `naturalKey` value was explicitly ruled out — SCD2 hasn't been used in
production, so there's nothing to preserve.

The design is in `architecture/implementation/todo/phase-068-per-mapping-pipeline-settings.md`.
