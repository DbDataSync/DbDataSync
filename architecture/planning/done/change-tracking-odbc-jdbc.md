# Change tracking — ODBC and JDBC

**Status: resolved 2026-08-27 — see Outcome at the end.** Read `change-tracking-strategies.md` and
`script-generated-change-queries.md` first — the second one is most of the answer here, and this
document is mainly about what is *left over* once scripting has covered the general case.

Neither driver exists yet; `additional-database-drivers.md` puts them at phases 25–26 and notes that
both are "*meta*-drivers reaching an arbitrary engine, so neither can assume a dialect".

## The honest position

**No native change-tracking reader is possible for either, in general.** This is not a gap to be closed
by more work — it is a fact about what an ODBC DSN or a JDBC URL is. The engine behind it might be
Postgres, DB2, Informix, Snowflake, Teradata, an AS/400 journal, or something written in 1994. There is
no query we can write that works across that set.

So the question is not "how do we build CDC for ODBC" but "what can an operator with an ODBC connection
actually get", and the answer is better than it first sounds:

| | available | how |
| --- | --- | --- |
| watermark mode | **now** | the generic `Watermark` reader, already built (phase 17), already dialect-driven |
| full/segmented reload | **now** | `BatchReload` + `DeleteInsert`, already built (phase 18) |
| real change tracking | via script | `ScriptedChangeQuery` — the operator supplies the engine's own SQL |
| catalog metadata | via script, or JDBC's own | see below |

The middle row is the important one, and it is worth restating the framing from the scripted-queries
doc: **that is not a lesser option.** If the source behind the DSN is Postgres, the operator writes
`pg_logical_slot_peek_changes(...)` and gets exactly what a native Postgres reader would give them. The
SQL is the same SQL; only the author changes.

## What is genuinely ODBC/JDBC-specific

Three things that the scripted-query doc raises but cannot settle, because they belong to the drivers:

### 1. There is no dialect to infer

`SqlDialect` (phase 17) covers quoting, parameter placeholders, qualifying a table and switching
database. An ODBC connection to Postgres wants `"x"`; one to MySQL wants `` `x` ``; one to SQL Server
wants `[x]`. **None of that is knowable from the DSN.**

Options, in increasing order of effort:

- **Configuration.** A `dialect` property on the connection, chosen by the operator from the dialects
  the codebase already has. Cheap, honest, and wrong only if the operator picks wrong — which they will
  find out immediately.
- **Probe.** ODBC's `SQLGetInfo` reports `SQL_IDENTIFIER_QUOTE_CHAR` and a DBMS name; JDBC's
  `DatabaseMetaData` reports `getIdentifierQuoteString()`, `getDatabaseProductName()` and rather more.
  Enough to *construct* a dialect at connect time for the common cases.
- **Both** — probe, and let configuration override it.

The probe path is genuinely attractive for JDBC, where `DatabaseMetaData` is rich and standard. For ODBC
it is thinner but `SQL_IDENTIFIER_QUOTE_CHAR` alone covers the single most important variation.

Worth noting this is the first case where a `SqlDialect` would be *constructed at runtime from probed
values* rather than being a compiled-in singleton like `MsSqlDialect.Instance`. The abstraction supports
it — nothing about `SqlDialect` requires it to be a singleton — but every existing implementation is
one, so it is an assumption worth checking before relying on it.

### 2. Positional parameters

Both bind `?` positionally. `SqlDialect` models named placeholders — `ParameterReference("p")` and
`ParameterName("p")` — because every engine so far has them.

A positional dialect can render `?` from `ParameterReference` and an empty name from `ParameterName`,
and it works **if and only if parameters are added to the command in the same order they appear in the
text**. That is currently true by construction in the generic pipeline (`SegmentScope` builds
placeholders and parameters in one pass, `StagingStatement` numbers them `__s{row}_{value}`), but it is
true by accident rather than by contract.

If ODBC/JDBC are going to rely on it, it should become an explicit rule with a test, rather than a
property that holds until someone reorders a parameter list. The alternative is a positional mode on
`SqlDialect` that makes ordering the dialect's job.

### 3. Metadata

- **JDBC** has `DatabaseMetaData` — `getTables`, `getColumns`, `getPrimaryKeys` — which is standard,
  works across engines, and is genuinely better than guessing at `information_schema`. `ITableCatalog`
  (phase 18) is exactly the seam for it: a `JdbcCatalog` implementing that interface, and nothing else
  in the pipeline changes.
- **ODBC** has `SQLTables` / `SQLColumns` / `SQLPrimaryKeys`, but driver support for them is uneven —
  some ODBC drivers implement them poorly or not at all. So ODBC needs the scripted metadata provider as
  a fallback in a way JDBC largely does not.

`InformationSchemaQueries` (phase 18) is worth trying first for both, since it works on any engine that
has one, and falling back to the driver's own metadata API when it does not.

## The one thing worth building natively: a generic trigger shadow table

There is one change-tracking mechanism that could plausibly work across the ODBC/JDBC surface without a
script: a shadow table maintained by triggers.

The read side is completely generic — `SELECT … FROM <shadow> WHERE seq > ? ORDER BY seq`, joined back
to the base table, which is the same shape as the Postgres and MySQL trigger fallbacks and as
`MsSqlChangeTrackingReader`. Nothing engine-specific there at all.

The *setup* side is not. `CREATE TRIGGER` syntax diverges more than almost anything else in SQL —
PL/pgSQL functions, MySQL's inline body, Oracle's `BEFORE/AFTER … FOR EACH ROW` with `:NEW`/`:OLD`, T-SQL's
statement-level `inserted`/`deleted` pseudo-tables. There is no portable trigger DDL.

Which suggests the useful split:

- **the reader is generic and built once** — `TriggerAudit`, an unprefixed Kind, dialect-driven, usable
  by every driver including ODBC and JDBC
- **the DDL is per-engine or scripted** — each driver that wants it supplies its trigger template; ODBC
  and JDBC supply it by script

That is a genuinely good deal: one reader implementation serving Postgres, MySQL, Oracle, ODBC and JDBC,
with only the enablement varying. It may well be worth building *before* any of the log-based readers,
since it delivers delete detection to every engine at once.

Whether DataSync should be *creating triggers on someone's source* at all is a separate and real
question. Doing it silently would be wrong; offering it as an explicit, previewable, opt-in action with
the generated DDL shown before it runs is defensible — and is the same interaction shape as the
scripted-query Test action.

## `ConnectionConfig` does not fit either of these

Already recorded in `additional-database-drivers.md` as a phase-22 question, restated here because
change tracking depends on it: `ConnectionConfig` models `Host`/`Port`/`Database`. ODBC wants a DSN or a
full connection string; JDBC wants a URL. `Properties` might carry it, or the model may need a genuine
alternative shape. Nothing in this document can proceed far without that being settled.

## Suggested order

1. **Phase 22's connection model** — nothing here works without it.
2. **The generic `TriggerAudit` reader**, with per-engine DDL for the engines that have drivers. Gives
   delete detection broadly, and is the cheapest large win in the whole change-tracking set.
3. **`ScriptedChangeQuery`** (its own doc) — after two native readers exist to generalise from.
4. **The ODBC and JDBC drivers themselves**, arriving to a world where both of the above already work.

## Open questions

- **Probe the dialect, configure it, or both?** Leaning both, probe-first for JDBC.
- **Is parameter ordering a contract or an accident?** It is currently an accident. If ODBC/JDBC depend
  on it, it needs a test that would fail if someone broke it.
- **Should DataSync create triggers on a source at all?** A product question more than a technical one,
  and worth an explicit answer before anyone builds the enablement side.
- **Does `ClrKernel.Database.Provider.Jdbc` surface `DatabaseMetaData`** through its ADO.NET wrapper, or
  only the `DbConnection` surface? If only the latter, the JDBC metadata advantage above evaporates and
  it needs the scripted provider like ODBC does. Worth checking early — it changes the plan.

---

# Outcome — resolved 2026-08-27, in two parts

**The generic trigger-audit reader became `implementation/todo/phase-033-trigger-audit-change-tracking.md`.**
This doc's own assessment — "the cheapest large win in the whole change-tracking set" — is why it was
promoted ahead of the Postgres phase: the read side is engine-neutral, so one reader delivers delete
detection to SQL Server, Postgres, MySQL, Oracle and anything behind ODBC or JDBC at once, with only
the `CREATE TRIGGER` DDL per engine.

The split it proposed is the shape the phase takes: generic reader, per-engine DDL through phase 25's
provisioning flow, and script-supplied DDL for engines we cannot know.

It also settled the product question this doc raised — *should DataSync create triggers on someone's
source at all?* — by pointing at phase 25, which already answered it for the whole class of DDL:
previewable, applied deliberately, never silent.

**The ODBC and JDBC drivers themselves are still unwritten**, and are tracked in
`planning/done/additional-database-drivers.md`'s table rather than here. Phase 31 removed their
blocker — `ConnectionConfig` now takes a DSN or a URL, and `AuthMode` has `None` for DSN-stored
credentials and wallets. What remains for them:

- **dialect probing.** `SQLGetInfo`'s `SQL_IDENTIFIER_QUOTE_CHAR`, JDBC's `DatabaseMetaData`. This doc
  observed that a `SqlDialect` would be *constructed at runtime* for the first time; phase 29's
  `IDialectProvider` is now the seam, and a driver that names no dialect already declines it cleanly.
- **positional parameters.** Still true that ordering is an accident rather than a contract, and still
  worth a test before anything depends on it.
- **metadata.** Phase 29's scripted metadata provider is the answer for a driver whose catalog does not
  work, and it exists now.
