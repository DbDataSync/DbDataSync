# Phase 179N: a structured editor for `UrlTemplate`/`ConnectionStringKeys` in the JDBC driver form

**Status: done (2026-09-23).** Second of five phases for the JDBC/driver-editing UI round requested
2026-09-23 — the
**first priority** of the round. Builds on 178N (the schema has to exist and round-trip before an editor
for it means anything). Closes the "whether `urlTemplate` deserves a real field" open question the
[url-template-and-connection-testing design](../../planning/todo/jdbc-url-template-and-connection-testing.md)
left open, and the design note that `urlTemplate` is "closer in spirit to `driverClass` than to a
rarely-touched key-spelling override."

## What ships

In `DriverEditPage.tsx`'s JDBC branch (`form.base === 'jdbc'`, currently just "Driver class" + the jar
picker, lines 187-219), add a labeled block after "Driver class":

- **URL template** (`driver-edit-url-template`) — a single-line text input, placeholder text showing the
  shape (`jdbc:postgresql://{host}:{port}/{database}`), bound to `form.urlTemplate`.
- **Inline placeholder legend**, static text under the field, not a tooltip — the four supported tokens
  (`{host}` `{port}` `{database}` `{username}`) plus one line making the `{password}` exclusion explicit
  ("never `{password}` — the password is always sent as a property, added automatically"), so an operator
  sees the rule before hitting 178N's constructor error, not only after.
- **Connection-string keys**, a collapsed-by-default sub-section (`<details>`, matching
  `ConnectionTestCard.tsx`'s own `ResolvedConnectionDetails` pattern already in this codebase) with six
  optional text inputs — `host`/`port`/`database`/`username`/`password`/`connectTimeout` — each showing
  its `JdbcGenericDriver.DefaultConnectionStringKeys` value as placeholder text (`host`, `port`,
  `database`, `user`, `password`; no default `connectTimeout` or `integratedSecurity` key today, so those
  two placeholders read "not set"), left blank meaning "use the default." Collapsed because, per 178N's
  design note, this is "a rarely-touched key-spelling override" for most operators — the template is the
  field people actually reach for.

## Form/assembly plumbing

`FormState` (`DriverEditPage.tsx:15-26`) gains:

```ts
urlTemplate: string
connectionStringKeys: { host: string; port: string; database: string; username: string; password: string; connectTimeout: string }
```

`ParsedDriverYaml`/`parseDriverYaml`/`assembleDriverYaml` (`driverYamlAssembly.ts`) read/write these from
the `jdbc:` block's `urlTemplate:` scalar and `connectionStringKeys:` nested block (six `scalarValue`
lookups scoped to that sub-block, mirroring how `driverClass`/`driverJarPaths` are already scoped to
`jdbcBlock`), and `assembleDriverYaml` emits `urlTemplate:` and a `connectionStringKeys:` block **only
for the keys the operator actually typed** (blank stays omitted, so the default keeps applying — the one
place this needs care: emitting an empty string as `host: ""` would be a real, different value from "not
set," not equivalent to omitting the line).

This changes what `jdbcExtra` (178N) captures: once `urlTemplate`/`connectionStringKeys` have real
structured fields, they move from "whatever extra lines happen to be in the block" to owned fields, the
same way `driverClass`/`driverJarPaths` already are — `jdbcExtra`'s filter (178N, § 3) needs its skip-list
extended from two keys to `driverClass`/`driverJarPaths`/`urlTemplate`/`connectionStringKeys`. An
operator's genuinely-custom extra key (something with no structured field at all) still round-trips
through `jdbcExtra` exactly as 178N designed — this phase only narrows what counts as "extra."

## Validation surfaced before save, not just after

178N's constructor throws `NotSupportedException` for a `{password}` template on `Create`/`UpdateYaml` —
`DriversController.TryBuild`'s existing catch (`DriversController.cs:178`) already includes
`NotSupportedException` in its allowlist, so this reaches the operator as a clean `BadRequest` with no
backend change needed. Confirm this with a test (`DriversController` or a Web.Tests spec): saving a JDBC
driver whose URL template field contains `{password}` shows `ErrorBanner` with the constructor's own
message, not a raw 500 or a silent failure.

## Out of scope here

- The raw-YAML-editor toggle (180N) and the validate/echo panel (181N) — this phase adds one feature to
  the existing structured form, not the alternate editing modes.
- Per-vendor template proving (Oracle's `@//host:port/service`, SQL Server's `;databaseName=`) — the
  design doc's own open question, unrelated to whether the *editor* for the mechanism exists.

## Applied — one real deviation from the design above

The doc's own "narrow `jdbcExtra`'s skip-list" plan turned out to be one step short: a hand-authored
`connectionStringKeys.integratedSecurity` (a key with no structured field) and a structured field
(`username`, say) being set **at the same time** would have produced two `connectionStringKeys:` blocks
in the assembled YAML — a real duplicate-key bug, found while writing the test for it, not assumed. Fixed
by splitting the leftover into its own `connectionStringKeysExtra` field (not embedded in `jdbcExtra`), so
`assembleDriverYaml` merges structured fields and unmodeled ones under **one** header. Covered by
`driverYamlAssembly.test.ts`'s "coexist under a single header" test.

## How to verify when closed

- A new JDBC driver authored entirely through this form (no raw-YAML fallback) round-trips
  `urlTemplate`/a non-default `connectionStringKeys.username` through save → reload unchanged.
- Typing `{password}` into the URL template field and saving shows the constructor's own error message in
  `ErrorBanner`, not a generic failure.
- A `driver.yaml` hand-authored with `connectionStringKeys.integratedSecurity` set (a key this form has no
  input for) still round-trips via `jdbcExtra`, confirming 178N's fallback still covers what 179N doesn't
  model.
