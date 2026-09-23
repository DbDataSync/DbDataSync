# Phase 181N: a validate-and-echo tool — show the JSON a `driver.yaml` actually resolves to, plus errors

**Status: done (2026-09-23).** Fourth of five phases for the JDBC/driver-editing UI round requested
2026-09-23. Builds
on 178N (JDBC fields worth echoing back must exist first) and pairs naturally with 180N (raw mode has no
structured-field errors to lean on, so this is its main feedback loop) — sequenced after both for that
reason, though it also improves 179N's structured mode (the operator can confirm a template placed a
value where they expected, before ever running a real connection test).

## The gap

Today the only feedback on a `driver.yaml`'s correctness is the save call itself
(`DriversController.TryBuild`, already parse-then-build entirely in memory before touching disk —
`DriversController.cs:158-183`) — an operator finds out a descriptor is broken, or finds out what a
template/typeMap/key-override actually resolved to, only by attempting to save (or worse, only by
testing a real connection afterward). There's no "show me, without committing" step, and no way to see
the *interpreted* shape at all — the raw YAML is the only view of what a template placeholder, a
default-falling-back key, or a typeMap entry's normalized form actually mean.

## Backend: `POST /api/drivers/validate`

Same in-memory parse-then-build `TryBuild` already does, exposed as its own endpoint rather than folded
into `Create`/`UpdateYaml` — this call must never write to disk and must work for a driver that doesn't
exist yet (a brand-new, unsaved id) and one that does (re-validating an edit before save).

```csharp
[Authorize(Policies.Admin)]
[HttpPost("validate")]
public ActionResult<DriverValidationResult> Validate([FromBody] DriverYamlRequest body)
{
    var (_, descriptor, error) = TryBuild(body.Yaml);
    return Ok(new DriverValidationResult(error is null, error, descriptor is null ? null : Preview(descriptor)));
}
```

`TryBuild` already returns `descriptor` even when the *build* step (not the parse step) fails
(`DriversController.cs:173-182`'s catch still has `descriptor` in scope) — so a descriptor that parses as
YAML but fails to build (a missing library, a bad `base` type name, 178N's `{password}` rejection) still
gets an interpreted echo alongside the error. Only a YAML that doesn't parse at all has no descriptor to
echo. That's the useful case to preserve: "here's what I understood before I hit a problem" is more
actionable than "error" alone.

`Preview(DriverDescriptorYaml)` builds a plain, JSON-friendly DTO — deliberately **not** a reflection dump
of the live `IDriver`/`Spec` (those carry non-serializable members like the resolved `DbProviderFactory`,
and reflecting a JDBC spec would touch IKVM types from `DbDataSync.Api`, the exact mistake 176M's
correction moved away from). Built straight from the descriptor, applying the same default-resolution
each real code path already applies, so what's shown is provably what would be used, not a guess:

```csharp
public sealed record DriverValidationResult(bool Valid, string? Error, DriverInterpretationPreview? Interpreted);

public sealed record DriverInterpretationPreview(
    string Id, string DisplayName, string Base,
    DialectPreview Dialect, IReadOnlyDictionary<string, string> TypeMap,
    IReadOnlyList<string> Readers, IReadOnlyList<string> Staging, IReadOnlyList<string> Writers,
    JdbcPreview? Jdbc);

public sealed record DialectPreview(
    string QuoteIdentifier, string ParameterPrefix, string RowLimit, string Catalog,
    string DefaultDatabase, int? DefaultPort, ConnectionStringKeysPreview ConnectionStringKeys);

public sealed record ConnectionStringKeysPreview(
    string Host, string? Port, string Database, string Username, string Password,
    string ConnectTimeout, string? IntegratedSecurity);

// UrlTemplate: null if the yaml didn't set one (still buildable — Jdbc.CreateConnection would then
// require connection.ConnectionString instead, same as today). ConnectionStringKeys: JDBC's own
// defaults (host/port/database/user/password) shown resolved when the yaml didn't override them, so
// "what key does this template actually place {host} under" never requires reading source.
public sealed record JdbcPreview(
    string DriverClass, IReadOnlyList<string> DriverJarPaths, string? UrlTemplate,
    ConnectionStringKeysPreview ConnectionStringKeys);
```

`TypeMap` is rendered as native-name → a short human string per entry (`"Int32"`, `"Decimal(precision=p,
scale=s)"`, `"String(length=n, unicode=true)"`) rather than echoing `TypeMapEntryYaml`'s own JSON shape
raw — the point of "what is actually being interpreted" is legibility, and the four DSL shapes
(`driverYamlAssembly.ts`'s `RAW_BODY_SKELETON` comment already enumerates them) are few enough to hand-
format clearly. `Catalog` resolves `"information_schema"` / `"query"` / `"java.sql.DatabaseMetaData"` —
whichever `DescriptorCatalogResolution` would actually pick, not the raw YAML string (which may be
omitted).

## Frontend: a validate panel, shared across both editor modes (179N structured, 180N raw)

A collapsible card below the editor (`driver-edit-validate-panel`), with a **Validate** button
(`driver-edit-validate-button`) that POSTs the *current* would-be-saved YAML — `assembleDriverYaml(form)`
in structured mode, `rawText` directly in raw mode, so it always validates exactly what Save would send,
never a stale load. On response: a green "Valid" line plus a `<pre>`-rendered, indented
`JSON.stringify(interpreted, null, 2)` block when `valid`; a red error line (the same message
`ErrorBanner` would show on a failed save) when not, still showing the JSON block underneath if
`interpreted` came back non-null (the "here's what I understood before the problem" case above).

Not run automatically on every keystroke — a debounced live-validate would be nice but is real added
complexity (request cancellation, staleness against fast typing) for a first version; a manual button
matches "Test connection"'s own existing on-demand shape in this same page family
(`ConnectionEditPage`'s `Test connection` button) and is enough to satisfy "UI tools to validate and echo
back... with any known errors" as asked.

`useValidateDriverYaml()` in `api/hooks.ts` — a `useMutation`, same shape as `useTestConnection`, posting
to `/api/drivers/validate`.

## Out of scope here

- Auto-revalidating on every change (see "not run automatically," above).
- Making `ConnectionsController.Test`'s real connection test call through this same preview — that's a
  different, already-shipped thing (`ConnectionTestCard`'s `ResolvedConnectionDetails`, 177M); this tool
  is for the driver descriptor itself, before any connection exists to test.

## Applied — a real phase 178N bug found building this, not assumed

Writing the test that asserts an **unmodified** `connectionStringKeys` field (the `host` key, not
overridden) exposed a real production bug in phase 178N's own schema: `JdbcDescriptorYaml.ConnectionStringKeys`
had reused `DescriptorConnectionStringKeysYaml` (the ADO.NET dialect's own type) verbatim. That type's
C# properties carry non-nullable, ADO.NET-flavoured defaults (`Host = "Host"`, `Password = "Password"`,
etc.) — so a yaml overriding *only one* field (`username`, say — exactly the shape phase 179N's own form
writes for a single-field override) deserialized with every other field already populated at its ADO.NET
default rather than left unset, silently applying the wrong key spelling to properties like `host`/
`password` the operator never touched.

Fixed at the schema level: a new `JdbcConnectionStringKeysYaml` type with every field genuinely nullable
and no default, so `JdbcGenericDriver.FromDescriptor` can fall back to `DefaultConnectionStringKeys`
per field rather than per block. Covered by a new regression test,
`JdbcDescriptorTests.APartialConnectionStringKeysOverride_LeavesUnmodeledFieldsAtJdbcsOwnDefaults`,
against the real Postgres container — a template with no placeholders forces the fallback-to-property
path to actually run for every field, which is what exposes the bug (a template that places `{host}`
directly, as phase 178N's own original test did, never consults the key spelling for `host` at all and
would never have caught this).

## How to verify when closed

- A valid JDBC driver.yaml with a `urlTemplate` and a partial `connectionStringKeys` override shows the
  full resolved key set (overridden values plus JDBC defaults for the rest) in the JSON panel.
- A yaml missing a required field shows the same error `Create`/`UpdateYaml` would have shown, without
  writing anything to disk.
- A yaml that parses but fails to build (e.g. `base` naming an unresolvable type) shows both the error and
  whatever `Interpreted` preview could still be built from the parsed descriptor.
- Clicking Validate never changes `restartRequired` state or touches `drivers/` on disk — confirmed by a
  test asserting no file-system write occurs.

## Closing note, 2026-09-23

Never browser/Playwright-verified — reachable, in a real browser, only through a JDBC-backed
driver.yaml, which `driver-authoring.spec.ts` deliberately never builds (no jar/`ikvm` fixture in this
suite). Same root cause as every other JDBC-touching Playwright gap in this repo, tracked once in
[`follow-up-jdbc-connection-failure-test-coverage-gaps.md`](../../planning/todo/follow-up-jdbc-connection-failure-test-coverage-gaps.md)
rather than repeated per phase. Not a reason to leave this phase open — the phase itself (types, wiring,
unit/API-level tests, `tsc -b`/build) is done; this is a named, tracked, bounded gap, not an unknown.
