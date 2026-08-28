# Phase 31 — Addressing connections that are not host and port

**Status**: Built
**Plan reference**: `architecture/planning/done/additional-database-drivers.md`, which names this as a
prerequisite for the ODBC and JDBC drivers and records it as unsettled:

> **`ConnectionConfig` does not fit ODBC or JDBC.** It models Host/Port/Database. ODBC wants a DSN or
> a full connection string; JDBC wants a URL. `Properties` (which gained a UI in phase 15) may be
> enough, or the shape may need a genuine alternative.
>
> **`AuthMode` is `SqlAuth | IntegratedAuth`.** Oracle wallets, Postgres certificate/SSL modes, MySQL
> auth plugins and JDBC's URL-embedded credentials do not fit.

Both are settled here, before the drivers that need them arrive — the same ordering phases 17–19 used,
so a new driver lands on an interface that already fits it.

## What this phase will build

### One addressing mode or the other, flat and nullable

`Host` stops being required and `ConnectionString` appears beside it:

```csharp
public required string Name { get; set; }
public required ConnectionDriverType DriverType { get; set; }
public string? Host { get; set; }
public int? Port { get; set; }
public string? ConnectionString { get; set; }
public string? Database { get; set; }
```

Flat and nullable rather than a nested `Address` object, for the reason phase 16 settled the same
question about endpoints: every connection written before this phase has a Host and no
`ConnectionString`, which reads back as "host mode" with no migration and no upgrade step.

**Exactly one of `Host` or `ConnectionString`**, validated at save. Both set is ambiguous — nothing
sensible decides which wins — and neither set is a connection that cannot connect. `Database` is
allowed alongside either, because "how do I connect" and "which database do I work in" are separate
questions and `SqlDialect.UseDatabaseAsync` already answers the second.

`ConnectionString` is the engine-native address: an ODBC DSN or full connection string, a JDBC URL, an
Oracle EZConnect or TNS name, or — usefully today — a SQL Server string carrying a failover partner or
`MultiSubnetFailover` that the Host/Port fields cannot express.

### The credential never goes in it

This is the part worth building carefully rather than documenting.

`CredentialSecretRef` exists so that config, which is git-committed and diffed in the UI, never holds a
plaintext password. A connection string is exactly the shape that invites one back in —
`...;Password=hunter2;...` — and it would be committed, pushed, and visible in the Version Control tab
forever.

So save-time validation **rejects a connection string containing a password key**, naming the key it
found and pointing at the credential field. The driver splices the resolved credential in at connect
time, on top of the operator's string, exactly as it does for host mode.

### `AuthMode` gains `None`

Not an enumeration of every engine's mechanism — wallets, `.pgpass`, Kerberos ticket caches, DSN-stored
credentials, auth plugins — because we would get that list wrong and be extending it forever. One
member that describes DataSync's side of it honestly:

```csharp
public enum AuthMode
{
    SqlAuth,          // a user id, and a credential from the secret store
    IntegratedAuth,   // the process's OS identity
    None,             // DataSync supplies no credential
}
```

`None` is what an Oracle wallet, a DSN with stored credentials, a `.pgpass` file and a
credential-bearing JDBC URL all look like from here: the address or the environment provides it, and
DataSync passes nothing. Anything more specific is a per-engine detail and belongs in `Properties`.

### The existing drivers honour it

`MsSqlDriver` and `PostgresDriver` build their connection strings from Host/Port today. Both gain the
connection-string path, which is not just plumbing for future drivers — it is the answer for a SQL
Server operator who needs `Failover Partner`, or a Postgres one who needs a specific `sslmode` and
certificate paths.

The credential is applied on top through the provider's own builder, so it is escaped correctly rather
than concatenated.

### SPA

The connection editor gets an addressing toggle — **Host & port** or **Connection string** — with the
irrelevant fields hidden rather than disabled, because a disabled Host field next to a connection
string invites the question of which one is being used. `AuthMode` gains its third option, and the
credential fields disappear under `None`.

## What this phase does not build

Any new driver or `ConnectionDriverType` member. Oracle, ODBC and JDBC follow.

Any per-engine validation of a connection string's *contents* beyond the password check. We cannot
know whether a JDBC URL is well-formed for a driver we have not written, and phase 19's connection test
already answers "does this actually work" better than a parser could.

## How to verify when built

- `ConfigValidation` unit tests: neither address rejected; both addresses rejected; a connection string
  carrying `Password=`, `PWD=` or `pwd =` rejected naming the key; one carrying `Persist Security
  Info` or a `password` *value* not falsely rejected.
- Round-trip tests: a connection written before this phase (Host, no `ConnectionString`) loads
  unchanged; a connection-string connection round-trips.
- `Category=Integration`: a SQL Server connection expressed as a connection string connects, and
  phase 19's test endpoint reports it reachable; the same for Postgres.
- A test that the credential is applied on top of an operator's connection string rather than being
  expected inside it.
- Playwright: switch the editor to connection-string mode, save, reload, and test the connection.
- Full suite green.

## Open questions

- **Should `Properties` still apply in connection-string mode?** It does today by being merged into the
  builder. Keeping that is consistent and lets an operator override one setting without rewriting the
  string; the alternative — the string is the whole truth — is easier to reason about. Leaning towards
  keeping it, since the merge order is already defined.
- **Browsing with no `Database`.** The SPA's cascade starts at the database list. A DSN pointing at one
  database has nothing to list. Phase 29's scripted metadata provider is one answer; a driver
  reporting a single database is another.

---

# Retrospective

Built as planned. One real bug was found by writing the test for the feature, and it was in the order
of operations rather than in anything new.

## A rejected save had already written the secret

`ConfigValidation.ValidateAddressing` was first added at the point in `SaveConnection` where the config
object gets built — which is *after* the `AuthMode.SqlAuth` block, and that block does
`_secrets.Store(secretRef, input.Password)`.

So a connection rejected for carrying a password in its connection string had already written a
password to the secret store on its way to being rejected. The validation designed to keep a credential
out of the wrong place was itself running after the credential had been put somewhere.

Found because the test asserting the rejection got a *different* rejection message —
`"UserId is required when AuthMode is SqlAuth"` — which was the honest signal that something ordered
before it was doing work.

Validation now runs at the top of the method, before anything with a side effect, and
`AConnectionStringCarryingACredential_IsRejectedAtSave` asserts the connection does not exist
afterwards as well as asserting the 400.

## The credential check matches keys, not text

The naive check — does the string contain "password" — rejects a database called `PasswordVault` and a
DSN whose path mentions one. It also misses `PWD=` and `pass_word=`.

Segments are split on `;`, the token before `=` is taken as the key, spaces and underscores are
stripped, and the result is matched against a list. So `Persist Security Info=True` passes (it carries
no secret despite the name), `Initial Catalog=PasswordVault` passes (a value is not a key), a JDBC URL
with no key/value segments at all passes, and `Password`, `PWD`, `pass_word`, `Secret` and
`Password = x` with spaces are all rejected naming the key that was found.

## `AuthMode.None` rather than an enumeration of mechanisms

The planning doc listed Oracle wallets, Postgres certificate modes, MySQL auth plugins and
URL-embedded credentials as things `SqlAuth | IntegratedAuth` does not fit. The temptation is to add a
member per mechanism, and that list would have been wrong the day it was written.

`None` describes **DataSync's side** of all of them: the address or the environment provides the
credential and we pass nothing. A wallet, a DSN with stored credentials, a `.pgpass` file and a
credential-bearing JDBC URL are one case from here, and anything more specific is a per-engine detail
that `Properties` already carries.

## The existing drivers got a feature, not just plumbing

Both `MsSqlDriver` and `PostgresDriver` take a connection string now, and it is immediately useful:
`Failover Partner`, `MultiSubnetFailover`, a specific `sslmode` with certificate paths — none of which
Host and Port can express.

An operator's string is the **base**, not the whole truth. The credential goes on top through the
provider's own builder, so it is escaped correctly rather than concatenated, and `Database` and
`Properties` apply in both modes identically.

SQL Server's two required defaults (`TrustServerCertificate`, `MultipleActiveResultSets`, both with
phase 3 comments explaining why) are applied **only where the operator has not already set them** —
`ShouldSerialize` is the check. A string that deliberately turns MARS off meant it, and silently
overriding it would be worse than the default not being there.

## Flat and nullable, again

Same call as phase 16's endpoints, for the same reason: every connection already on disk has a `Host`
and no `ConnectionString`, which reads back as host mode with no migration and no upgrade step.
`AHostConnectionWrittenBeforeThisPhase_StillLoadsAndWorks` is the check.

## Verification

- `ConnectionAddressingTests` — 16 cases: each mode alone accepted, both rejected, neither rejected,
  whitespace not an address, six credential spellings rejected, four look-alikes accepted, and the
  message naming the key.
- `ConnectionStringAddressingTests` (`Category=Integration`) — 6 tests against real servers: a
  connection-string connection saves, round-trips without the credential in it, and reports reachable
  through phase 19's endpoint; the credential applied on top of a string that has none; a credential in
  the string rejected *and nothing written*; neither address rejected; a host connection unaffected;
  and Postgres by connection string.
- Playwright test 17 — switch mode in the editor, confirm Host disappears rather than greys out, save,
  reload in the saved mode, and test the connection.
- Full .NET suite green: 496 tests. Playwright: 17 green. `tsc -b` clean, `oxlint` unchanged at four.

## Open questions, one answered

- **`Properties` still applies in connection-string mode.** Kept, because the merge order was already
  defined and it lets an operator override one setting without rewriting the string.
- **Browsing with no `Database`** is still open, and now blocks nothing until a DSN-shaped driver
  exists. Phase 29's scripted metadata provider is one answer; a driver reporting a single database is
  another.

## What this unblocks

Oracle, ODBC and JDBC batch reading — the ask this phase was a prerequisite for. Oracle can now be
addressed by EZConnect or a TNS name, and ODBC and JDBC have somewhere to put a DSN and a URL.
