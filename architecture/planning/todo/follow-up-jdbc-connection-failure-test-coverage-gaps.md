# Follow-up: two real-failure paths in the JDBC connection stack have no test coverage

Two gaps phases 175M-177M's own docs each had to leave open and admit, not hypothesized here — this
collects them in one place since both are the same underlying shape ("a real JDBC failure mode with no
way to trigger it deterministically in this test suite today") and are worth picking up together.
Documented to fix later, not fixed here.

## 1. `JdbcConnection.Open()`'s soft `isValid` check — both negative branches untested

`Open()` calls `_connection.isValid(JdbcValidateTimeoutSeconds)` after connecting and treats a `false`
result as a failed open, but a `java.sql.SQLException` from that call as merely inconclusive:

```csharp
try
{
    if (!_connection.isValid(JdbcValidateTimeoutSeconds))
        throw new InvalidOperationException($"The JDBC driver reported the new connection to '{jdbcUrl}' as invalid.");
}
catch (java.sql.SQLException)
{
    // Inconclusive, not fatal — see the comment above.
}
```

Every test in `DbDataSync.Drivers.Jdbc.Tests` opens a real, genuinely healthy connection against the real
Postgres container this project already runs — `isValid` always returns `true` on that path, so neither
branch above has ever actually executed under test. Phase 175M's own doc names this as a gap and rules out
mocking `java.sql.Connection` (a large interface over IKVM interop, no precedent anywhere in this test
project for mocking a `java.sql.*` type).

**A real technique exists, though, and doesn't need any mocking.** `JdbcTestDatabase.OpenNpgsqlConnection`
already gives every test in this project a second, independent connection to the same real Postgres
server the JDBC connection under test is using. Postgres's own `pg_terminate_backend(pid)` — called from
that *second* connection, targeting the JDBC connection's own backend PID (`SELECT pg_backend_pid()`, run
once through the JDBC connection itself right after opening, before severing it) — closes the underlying
server-side session out from under the JDBC connection while the C# side still believes it's open. A real
pgJDBC driver detects that on the next `isValid` call — this is exactly the scenario `isValid` exists to
catch, not a contrived one. Sketch:

```csharp
[Fact]
public async Task Open_AgainstAConnectionSeveredServerSide_ReportsInvalidOrThrowsSoftly()
{
    // Open once, capture the backend PID, then kill that backend from a second, independent connection
    // — the real failure isValid's soft catch exists for, not a mock standing in for one.
    var pid = /* SELECT pg_backend_pid() through a throwaway connection */;
    using (var admin = db.OpenNpgsqlConnection())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText = $"SELECT pg_terminate_backend({pid});";
            await cmd.ExecuteNonQueryAsync();
        }
    // ... now open a *new* JdbcConnection is not what we want — we want the ALREADY-OPEN one's next
    // isValid call to observe the severed backend. Needs a driver/connection shape that lets a test
    // call Open() again or otherwise re-trigger isValid on the same live connection — worth checking
    // whether that's reachable through the public API as it stands, or needs a small seam.
}
```

That last point is the real design work here: `isValid` is only ever called from inside `Open()`, once,
right after connecting — there's no public way to re-trigger it on an already-open connection to observe
a *mid-life* failure the way the scenario above needs. Two honest options for whoever picks this up:
sever the backend *before* the JDBC side's own `Open()` call reaches `isValid` (a timing race against the
initial connect, likely too fragile to be a reliable test), or expose a small internal seam
(`JdbcConnection`'s own test-only re-validate hook) — a real design decision, not sketched further here.

## 2. No browser-level proof of the JDBC-specific console flows

Phase 177M's own doc: "the JDBC-specific case (`jdbcUri` populated, a rejected-URL failure) has no
browser-level proof — no fixture in this suite opens a real JDBC connection through the console." This
isn't new to 177M — `driver-yaml-authoring-ui.md`'s own retrospective already named the identical gap for
the JDBC *create* path ("the JDBC create path has no Playwright coverage — needs a real jar plus `ikvm`
actually installed, neither set up in the test server"). Every JDBC-touching Playwright gap in this repo
traces back to the same root cause, not three separate ones — **including all of 179N-182N's own new UI**
(the `urlTemplate`/connection-string-keys structured editor, the raw-YAML editor mode, the validate/echo
tool, the broken-driver banner): every one of them is only reachable, in a real browser, through a
JDBC-backed driver.yaml, and `driver-authoring.spec.ts` deliberately never builds one, for the identical
reason named here. This doc is the single place that gap is tracked for every phase it touches
(175M-177M, 179N-182N, and `driver-yaml-authoring-ui.md`'s own JDBC create path) — not restated per phase.

**What's actually missing, concretely**: `playwright.config.ts`'s own scratch repo (what every Web.Tests
spec's `webServer` runs against) never has `ikvm` installed as a library, and never has a real JDBC jar
placed where a `driver.yaml`'s `jdbc.driverJarPaths` could reference it. `DbDataSync.Drivers.Jdbc.Tests`
already solves the "get a real jar" half of this for the *backend* suite (`DownloadJdbcTestJar`,
downloading pgJDBC from Maven Central into `$(BaseIntermediateOutputPath)jdbc-jars/` at build time) — the
same technique, wired into `global-setup.ts` instead of a `.csproj` target, would get the jar into the
Playwright scratch repo's `files/` store. Installing `ikvm` there is the same `POST /api/libraries` call
`admin-drivers-libraries.spec.ts` already uses for `MySqlConnector`, just naming `ikvm` instead.

**Worth doing as one piece of shared infrastructure, not per-spec.** Once a real JDBC-backed driver +
connection can exist in the Playwright scratch repo at all, it unblocks every currently-stubbed-out JDBC
UI flow at once: the driver-authoring JDBC create path, this doc's own `jdbcUri`/rejected-URL case, and
any future JDBC-facing screen — not just one test. Scoped as its own infrastructure item for that reason,
not folded into whichever feature happens to need it next.

## Where this applies

Both are `DbDataSync.Drivers.Jdbc.Tests`/`DbDataSync.Web.Tests`-only gaps — nothing here implies a product
bug, only untested paths.
