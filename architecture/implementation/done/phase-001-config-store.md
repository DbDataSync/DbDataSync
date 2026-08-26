# Phase 1 — Config Model & Git-Backed Store

**Status**: Complete
**Plan reference**: `architecture/implementation-plan.md` § Phase 1

## What was built

All new code lives in `src/DataSync.Core`, since both `DataSync.Api` (writes on save, Phase 5) and
`DataSync.TaskRunner` (reads a task's config at run start, Phase 4) need identical read/write/secret
behavior — putting it in a dedicated project would just be indirection with one real caller each.

**Domain models** (`Config/`): `ConnectionConfig`/`ConnectionInput`, `SchedulingConfig` (Continuous
w/ frequency or Periodic w/ cron, per `architecture/planning/done/architecture.md`),
`ChangeProcessingConfig` (`ReaderConfig`/`CacheConfig`/`WriterConfig`, each a string `Kind` +
`Options` dict — validated against real drivers starting Phase 3, not yet), `ReplicationTaskConfig`,
`TableMappingConfig` (+ `TableRef`/`SourceTableRef`/`ColumnMapping`).

**On-disk layout** (`ConfigPaths`), exactly as specified in `detailed-design.md` §3.6:
```
config/connections/<name>.yaml
config/replications/<name>/task.yaml
config/replications/<name>/table-mappings/<mapping-name>.yaml
```

**YAML I/O** (`YamlConfigSerializer`): YamlDotNet, camelCase naming convention, default values
omitted on serialize (keeps diffs minimal).

**Git auto-commit** (`Git/GitCommitService`, `Git/GitAuthor`): stages the changed file(s) and commits
via LibGit2Sharp immediately after every write — no separate publish step, matching the confirmed
"auto-commit on save" decision. `GitAuthor` (name/email) is supplied by the *caller* on every write
rather than fixed inside `ConfigRepository` — see "Decisions" below.

**Secrets** (`Secrets/SecretRefs`, wired into `ConfigRepository`): `ClrKernel.Core.Secrets.SecretStore`
resolves/stores/deletes credentials. `ConnectionConfig` (the serialized type) only ever has a
`CredentialSecretRef` string; the plaintext `Password` lives on `ConnectionInput` (a separate,
never-serialized type) and is written to `SecretStore` before the YAML is written, so there's no code
path where a plaintext credential can reach disk. Secret ref convention: `datasync:connection:<name>`.

**`ConfigRepository`**: the single façade — `Save`/`Load`/`List`/`Delete` for connections,
replication tasks, and table mappings. Every save validates the name (letters/digits/`-`/`_` only,
since it becomes a file/directory name), writes YAML, and commits.

## Decisions made this phase

- **`GitAuthor` is caller-supplied, not fixed inside `DataSync.Core`.** Real per-user attribution
  depends on auth, which is still an open question (`detailed-design.md` §8) — not resolved yet. The
  API layer (Phase 5) is where a real decision gets made (per-authenticated-user identity, or a
  placeholder system identity like `DataSync <datasync@localhost>` until auth exists); `TaskRunner`
  never writes config, so it never needs this. Not asked as a question this phase since it's cheap to
  change later and doesn't affect any of the persisted file formats.
- **Secret resolution is deliberately not part of config loading.** `ConfigRepository.LoadConnection`
  returns only the secret *reference*; nothing in the config layer ever turns that into a plaintext
  value. Only `DataSync.TaskRunner`, at actual connect time (Phase 4), calls `SecretStore.Resolve`
  directly. This keeps "read config" and "obtain a live credential" as separate, separately-auditable
  operations.
- **Reader/Cache/Writer `Kind` is an unvalidated string in Phase 1.** The driver registry that would
  let `ConfigRepository` reject an unsupported `Kind` doesn't exist until Phase 3. Config shape is
  validated now; cross-referencing against real driver capabilities is explicitly deferred, not
  forgotten.
- **`ClrKernel.Core.Secrets.SecretStore`'s actual API was inspected via reflection** (README + XML
  doc comments plus a throwaway reflection console app), rather than assumed from the package name.
  Confirmed surface used here: `SecretStore.ForProviders(ISecretProvider[])` (test-only, in-memory,
  never touches the OS keychain — used in all of this phase's tests so CI doesn't need a real
  credential store), `Store(key, value)`, `TryResolve(key, out value)`, `Resolve(key)`, `Delete(key)`.

## Verified

- `dotnet build` / `dotnet test` — full solution green, 18 tests total (14 new in
  `DataSync.Core.Tests`, 4 unchanged placeholders elsewhere).
- New tests cover: YAML file lands at the documented path; **no plaintext password ever appears in
  the written YAML file or in the git blob committed for it**; commit author name/email matches what
  was passed in; credential round-trips correctly through `SecretStore` (store via `SaveConnection`,
  resolve independently, as `TaskRunner` will do later); IntegratedAuth connections get no secret
  ref; saving SqlAuth without a password and without a pre-existing secret throws; invalid names
  (empty, containing spaces or `/`) are rejected; delete removes both the file and the stored secret
  and commits; replication task and table mapping save/load round-trip correctly, including the
  table-mapping file landing under the replication's own directory.

## Notes / things to revisit later

- Nothing in `DataSync.Api` or `DataSync.TaskRunner` calls `ConfigRepository` yet — that wiring
  happens in Phase 5 and Phase 4 respectively. This phase only had to prove the config layer itself
  is correct in isolation.
- No `Directory.Build.props`/central package management was introduced (same note as Phase 0); still
  not needed at this scale.
