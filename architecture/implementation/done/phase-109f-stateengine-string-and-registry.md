# Phase 109f — StateEngine as a string, and a StateDialect registry

**Status**: Done.
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*Custom state dialects as
an extension*. Depended on 109a (the same enum→string move, applied one layer down) and 109c (the
provider layer, for the non-built-in path — not actually needed by anything this phase built; see
Decisions). First phase below the line — it removes no dependency; it is the seam 109g and a future
custom state backend need.

## What this built

`StateEngine { Sqlite, MsSql, Postgres }` is gone. `ApiOptions.StateEngine`, `StateDialect.Engine`, and
every dialect's own `Engine` property are `string`. `StateDialect.For(string)`'s closed `switch`
became a lookup on a new `StateDialectRegistry` — so a custom `StateDialect` can be *registered*
rather than added to a fixed list. The three built-in dialects still register themselves in code and
still hard-reference their providers; nothing about a running deployment changes.

### `src/DbDataSync.State/`

- **`StateEngineIds.cs`** (renamed from `StateEngine.cs`) — `const string Sqlite/MsSql/Postgres`,
  mirroring `DbDataSync.Core.Config.DriverIds` exactly one layer down.
- **`StateDialectRegistry.cs`** (new) — `Register(StateDialect)`, `Get(string)`, `TryGet(string, out)`.
  A `static Default` property, built once, with the three built-ins pre-registered — see Decisions for
  why this is a static initialiser rather than the plan doc's other leaning (an explicit call from each
  composition root).
- **`StateDialect`** — `Engine` : `string`; `For(string engine) => StateDialectRegistry.Default.Get(engine)`.
  An unknown id throws `InvalidOperationException` naming the three built-ins and stating that anything
  else must be registered by a compiled plugin first.
- **`StateDatabase`** — the `(StateEngine, string)` constructor's first parameter is now `string`; the
  one internal branch that compared it to `StateEngine.Sqlite` now compares to `StateEngineIds.Sqlite`.
  The SQLite-only path constructor (`StateDatabase(string sqliteFilePath)`) is completely unchanged in
  shape — it still just calls `this(StateEngineIds.Sqlite, ...)` — which is why the ~30 test files and
  every production call site that only ever use *that* constructor needed no changes at all.
- **`StateDatabase.Factory.FromOptions`** — `engine` parameter is `string`; the SQLite short-circuit
  compares against `StateEngineIds.Sqlite` the same way.

### Validation moved, not duplicated

`ApiOptions.FromConfiguration` and `InviteCommand` each used to run their own
`Enum.TryParse<StateEngine>(..., ignoreCase: true, ...)`, silently falling back to `Sqlite` on anything
unrecognised — reasonable when the id space was closed and "unrecognised" could only mean a typo.
Now that the space is open (a custom dialect can be registered), "unrecognised" and "I meant a real
custom engine that just isn't registered yet" are indistinguishable at that layer, and silently
starting an empty SQLite store would hide either mistake. Both call sites now just read the raw
configured string (defaulting to `StateEngineIds.Sqlite` when absent) with **no validation at all** —
`StateDialect.For`, reached downstream through `StateDatabase.FromOptions` at actual startup, is the
one place that both needs a real answer and can give a useful error. `AdminConfigService`'s own
description of the `StateEngine` key was updated to match (it used to say "an unrecognized value falls
back to Sqlite"; it now says it refuses to start).

## What this phase does not build

- Removing `Microsoft.Data.SqlClient` / `Npgsql` from `DbDataSync.State.csproj` — 109g.
- Loading a `StateDialect` from a NuGet package — reuses 109e's compiled-plugin loader, folded into
  109h (or a small follow-up) once there is a real reason to want one.
- Any change to SQLite's own dialect or connection path — untouched, as planned.

## How it was verified

- `dotnet build DbDataSync.slnx` clean. Full non-integration suite green — `DbDataSync.State.Tests`
  went from 174 to 181 (the 7 new registry tests), no existing assertion changed. Full
  `Category=Integration` suite green too, including `CrossEngineStateTests` (every test in that file
  parametrized over all three engines via `TheoryData<string>`, unchanged in shape) against the real
  SQL Server and PostgreSQL containers.
- **New — `tests/DbDataSync.State.Tests/StateDialectRegistryTests.cs`**: the three built-ins resolve by
  their string id and report it back through `Dialect.Engine`; an unknown id throws naming all three
  built-ins; a fresh (non-default) `StateDialectRegistry` instance's `TryGet` returns `false` rather
  than throwing for an unregistered id; a `FixtureStateDialect` (delegates every member to
  `SqliteStateDialect.Instance` except `Engine`, since the built-ins are `sealed` with private
  constructors and cannot be subclassed) registers into a fresh registry and resolves by its own id;
  and — the assertion that proves this is a real extension point, not just a lookup table — the same
  fixture registered into `StateDialectRegistry.Default` lets a real `StateDatabase` construct against
  it and run the **full migration set**, verified by querying the `Tasks` table afterward.
- A deployment configured `StateEngine: MsSql` (or `Postgres`) still opens exactly as before — the
  built-in path through `StateDialectRegistry.Default` is unchanged code, and every existing
  `Category=Integration` state test already covers it.

## Decisions

- **Static initialiser, not explicit registration** — resolving the phase doc's own open question the
  other way from its stated leaning. The leaning ("explicit call — one obvious place, matches how
  `DriverRegistry` is populated") assumed threading a registry instance through call sites the way
  `DriverRegistry` is threaded through the API's DI container. But `StateDialect.For` had exactly two
  callers before this phase (`StateDatabase`'s enum-taking constructor, and one test fixture), and the
  overwhelmingly common path — `new StateDatabase(sqliteFilePath)`, ~30 call sites across production
  and tests — never touches `StateDialect.For` with anything but the fixed SQLite id. Threading an
  explicit registry through all of that for zero behavioural change would have been a far larger diff
  than the plan doc's own "nothing about a running deployment changes" framing calls for. A static
  `Default`, built once with the three built-ins, gets "works for `dotnet test` too" for free (the plan
  doc's own aside) while `StateDialectRegistry` itself stays a real, instantiable class — this phase's
  own test constructs a private instance to prove `TryGet`'s not-found path independent of the shared
  default, and a future compiled `StateDialect` plugin (109h) still has a genuine public
  `Register` to call on `Default` before anything asks for its id.
- **109c's provider layer turned out not to be needed by anything built here.** The phase doc listed
  109c as a dependency "for the non-built-in path" — `StateDatabase`/`StateDatabase.Factory` obtaining
  a non-built-in dialect's connection factory from `ProviderRegistry` instead of the dialect's own
  `CreateConnection`. That wiring is not exercised by anything this phase actually built: the three
  built-ins keep `CreateConnection` exactly as before (per the phase doc's own scope), and a custom
  dialect registered in this phase's own tests still supplies its own `CreateConnection` (delegating to
  SQLite's). Wiring a registered-but-not-built-in dialect through `ProviderRegistry` specifically is
  left for whenever a real custom `StateDialect` plugin (109h) exists to need it — nothing here
  forecloses that, and building it speculatively now would be exactly the kind of unrequested
  abstraction this project's own conventions warn against.
- **The `Dialect.Engine == StateEngineIds.Sqlite` fast-path in `StateDatabase.Command`** (skip
  parameter-placeholder rewriting for real SQLite) is keyed on the *literal id*, not "is this dialect
  SQLite-shaped" — confirmed deliberately rather than by accident: `StateDialectRegistryTests`'s
  fixture dialect (SQLite-shaped SQL, a different id) takes the *other* branch, rewrites `$name` through
  `Dialect.Parameter`, and produces byte-identical SQL anyway (SQLite's own `ParameterReference` already
  renders `$name`), which is why the full-migration-set test passes without needing this fast path to
  key on shape instead of identity. Worth stating plainly rather than leaving as an unexplained "why did
  this take longer" if someone hits it retracing this phase's own tests.

## Open questions — resolved

- **Static module initialiser vs. explicit call**: resolved in favour of the static initialiser — see
  Decisions above.
- **Migration note wording**: `AdminConfigService`'s description of `DbDataSync:StateEngine` now says
  plainly that an unrecognised value refuses to start, replacing the old "falls back to Sqlite" text
  that this phase's validation change made false.
