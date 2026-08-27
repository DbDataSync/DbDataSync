# Phase 23 — C# scripting host (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/csharp-scripting-host.md` and
`architecture/planning/done/csharp-script-extension-points.md`.

## What this phase will build

The spine: a script is stored, compiled, resolved and invoked. Plus **one slot**, chosen so the spine
is provable end to end — `sqlColumnExpression`, which generates a source-dialect SQL expression and
hands it to phase 22's `SourceProjection`.

That slot is the right first one because phase 22 just built the thing it plugs into, because it is
the cheapest at run time (the source evaluates the expression, we evaluate nothing), and because its
output is a *string* — so the whole compile-resolve-invoke path can be asserted without a database.

## Projects

```
DataSync.Scripting.Abstractions   contracts an operator implements. No Roslyn. Referenced by drivers.
DataSync.Scripting                the host: compiler, cache, loader, resolution. Roslyn lives here.
```

The split mirrors `Drivers.Abstractions` / `Drivers.*`, and exists for the same reason: a driver has to
be able to *invoke* a script without taking a compiler dependency.

**`SqlDialect` does not cross into the script surface.** It lives in `DataSync.Drivers.Generic`, which
would make the reference circular, and it is a moving target — it has gained four hooks in three
phases. Scripts get `IScriptDialect`: `QuoteIdentifier`, `ParameterReference`, `EngineName`. Narrow on
purpose, so a script written today still compiles when `SqlDialect` grows a fifth hook.

## Storage

```
config/scripts/<name>.cs      the code
config/scripts/<name>.yaml    the manifest
```

Git-tracked in the existing config store, so a script gets diff, blame, history and the auto-commit
per change that connections and mappings already have.

```yaml
name: pad-account-code
kind: SqlColumnExpression
entryType: PadAccountCode
description: Left-pads legacy account codes to 10 characters, in the source's own dialect.
parameters:
  - name: width
    required: true
enabled: true
```

## Compilation

`CSharpCompilation` into a collectible `AssemblyLoadContext`, with a closed reference set:
`System.Runtime`, `System.Linq`, `System.Collections`, `System.Text.RegularExpressions`,
`System.Globalization`, `DataSync.Scripting.Abstractions`.

Not a sandbox, and the planning doc says so at length. It is a guardrail that turns "I will just call
an HTTP API from inside this" into a compile error, which is a design conversation rather than a
production incident. The security decision — accepted, gated by permissions — is recorded there.

**Compile at save.** `PUT /api/scripts/{name}` compiles and returns 400 with Roslyn's diagnostics
(line, column, message) if it fails. A script that will not compile never reaches a run, which is the
rule phase 16 set for mappings.

**Cache to disk, keyed by content hash.** The TaskRunner is a process per run, so an in-memory cache
buys nothing — every run would start cold and pay Roslyn's first-compile cost before compiling
anything of ours. The cache lives beside the state database, is not git-tracked, and is safe to
delete. The API populates it on save, so a run normally loads a DLL and never compiles.

## Config model and resolution

Each of the three levels gains:

```csharp
public Dictionary<string, ScriptBinding?> Scripts { get; set; } = new();
```

`ScriptResolution.Resolve(slot, connection, task, mapping)` returns the most specific binding —
**mapping, then replication, then connection** — with the binding atomic: name and parameters replaced
together, never merged, so reading one level tells you what runs.

A **present key with a null value** means "explicitly none", overriding an inherited binding. An absent
key means inherit. A dictionary is what makes those two distinguishable; phase 16's endpoints never
needed the distinction because an endpoint is always required.

## The slot

```csharp
public interface ISqlColumnExpression
{
    /// Return null to leave the column alone.
    string? RenderSql(SqlColumnExpressionContext context);
}
```

The context carries the column's metadata, **the already-quoted, already-qualified column reference**
(the same string `{{column}}` substitutes, for the same reason), the binding's parameters, and
`IScriptDialect`.

`SourceProjection` gains an optional resolver. Precedence where both exist: **the mapping's literal
`Transform` wins over a script**, because a value typed into that row of the editor is the more
specific statement of intent and the operator can see it.

## What this phase does not build

The in-process transforms (`IValueColumnExpression`, `IRowTransform`) — phase 24. Metadata providers —
phase 25. Source query builders — phase 26. Target statements — later.

## How to verify when built

- `ScriptResolution` unit tests: inherit from connection, overridden by replication, overridden by
  mapping, explicit-none at each level, and unbound.
- Compiler tests: a good script compiles and its type is found; a bad one returns diagnostics with a
  line number; a script referencing a banned assembly fails to compile; the disk cache is hit on the
  second compile of identical source.
- `SourceProjection` tests: a scripted expression is rendered; a literal `Transform` beats a script.
- `Category=Integration`: a replication whose mapping binds a script lands transformed data.
- API: save invalid code → 400 with diagnostics; save valid → 200 and the cache is populated.
- Full suite green.

## Open questions

- **Where does the SPA editor come from?** A monospace textarea plus compile-on-save diagnostics is
  enough for a first cut; Monaco is a large dependency for a language service the diagnostics mostly
  replace.
- **Script parameters are `Dictionary<string,string>`** like every other options bag in this codebase.
  Consistent, and untyped. Fine until someone wants a list.
