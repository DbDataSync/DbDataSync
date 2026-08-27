# Phase 23 — C# scripting host

**Status**: Built
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

---

# Retrospective

Built as planned, with one design corrected during the build and one claim in the plan found to be
overstated.

## The script hook moved out of the reader, and that was the important decision

The plan put the slot inside `SourceProjection`: give the reader a resolved `ISqlColumnExpression`,
let it call the script while building its SELECT list. That was built, tested and then removed.

What replaced it: `RunExecutor` resolves the binding, runs the script, and folds the generated SQL into
`ColumnMapping.Transform` **before the reader sees the mappings at all**. The script is handed
`{{column}}` as its column reference, so what it emits is exactly what a human would type into the
mapping editor — and phase 22 then handles it unchanged, including the substitution that makes it
correct in the Change Tracking reader.

Four things fall out of that, and none of them is small:

- **No second path.** A generated transform and a hand-written one are the same thing by the time
  anything consumes them. The in-reader version would have meant two implementations of one feature,
  and the generated one would have been the path nobody could see.
- **The script runs once per pass**, not once per statement.
- **The generated SQL is loggable.** `RunExecutor` writes `Name → REVERSE({{column}})` into the run log,
  so an operator debugging a transform can read what the script actually produced.
- **The driver layer keeps no scripting dependency at all.** `DataSync.Drivers.Generic` briefly
  referenced `DataSync.Scripting.Abstractions`; it does not now.

The cost is real and worth stating: a script cannot see the *resolved* column reference, so it cannot
emit an expression referencing a second column and have that reference qualified correctly. That is the
same limitation a hand-written transform has, and cross-column work belongs to the row-transform and
query-builder slots.

## The reference set is a weaker guardrail than the plan claimed

The plan said a closed reference set makes reaching for the network or the filesystem a compile error.
Half true. Keeping an assembly out works for `System.Net.Http` and `System.Diagnostics.Process`, which
live in their own assemblies. It does nothing whatever about `System.IO.File` or `System.Environment`,
which live in `System.Private.CoreLib` **next to `string` and `int`** — there is no reference set that
admits one and not the other. Compiling against reference assemblies rather than implementation ones
would fix it properly, and is a larger change than it is worth for a boundary already decided against.

This was found by writing the test that asserted it and watching it fail.

`ScriptSyntaxGuard` closes the gap as a lint: a `using` of a banned namespace, or a spelled-out
`System.IO.File.ReadAllText`, is rejected at save with a message saying why. It is trivially bypassed —
by reflection, by an alias — and it is not trying to stop anyone. It turns the *likely accident* into a
save-time message. Both the code and the tests say which of the two it is, because a guardrail
described as a boundary is worse than no guardrail.

## The disk cache is the thing that makes this affordable

Restated from the plan because building it confirmed it: the TaskRunner is a process per replication
run, so an in-memory compile cache buys nothing. Every pass would start Roslyn — on the order of a
second — before compiling anything of ours, against a fifteen-second schedule. The API populates the
cache when it validates on save, so a run normally loads a DLL and never starts a compiler.

The cache writes to a temporary name and moves into place, because two processes compiling the same
script at once is ordinary here: the API validates on save while a run is already under way.

## "Explicitly none" is the one thing phase 16's model did not carry over

Endpoints are always required, so absent could mean inherit and nothing else. A script binding is not
required, so absent has to mean *inherit* while something else means *none* — otherwise a connection
that binds a transform can force it on every mapping beneath it forever.

A dictionary distinguishes them: an absent key inherits, a key present with a null value overrides with
nothing. The SPA's picker shows all three as three options, because they are three states and
collapsing two of them into a checkbox would lose one.

## Verification

- `ScriptCompilerTests` — 12 tests: valid code compiles and its entry type is found and constructed;
  broken code reports line and column; `System.Net.Http` is out by reference and `System.IO` by lint;
  a class of the operator's own named `File` is **not** falsely rejected; a missing entry type says
  what was actually there; a wrong contract fails at instantiation; the disk cache is hit on the second
  compile; and changing only the entry type is a different cache entry.
- `ScriptedColumnTransformTests` — 9 tests against genuinely compiled scripts: the generated expression
  lands in `Transform` in `{{column}}` form, a literal transform wins, a null return leaves the list
  un-copied, parameters and engine name reach the script, metadata reaches it when the caller has it,
  and a script that throws fails naming the column it was working on.
- `ScriptResolutionTests` — 8 tests: inherit, override at each level, explicitly-none at each level,
  atomic parameters, and slots resolving independently.
- `ScriptsControllerTests` — 8 tests: round-trip with the code byte-for-byte intact, a non-compiling
  script rejected **and not written**, a sandbox-breaking script rejected, an unknown kind rejected
  saying what is known, compile-without-saving in both directions, and the slot list.
- **Playwright test 15 is the whole loop through the UI**: write C#, compile it, watch a broken one
  produce diagnostics, bind the good one to a mapping with a parameter, clear the literal transform
  that was beating it, run, and read `tegdiW` out of the target while the source still holds `Widget`.
- Full .NET suite green: 324 tests across ten projects. Playwright: 15 green.

`BackfillIntegrationTests.TwoIdenticalBackfillTriggers_CollapseIntoOneRun` failed once during a
full-solution run and passed on every isolated and repeated run afterwards. It is a timing-sensitive
collapse test and the whole solution shares one SQL Server; recorded here rather than ignored, because
an intermittent test that nobody wrote down is one that gets re-diagnosed from scratch later.

## Not built, and deliberately

The in-process transforms (`IValueColumnExpression`, `IRowTransform`) — phase 24. Metadata providers —
phase 25. Source query builders — phase 26. Target statements — later.

The SPA editor is a monospace textarea. The compile diagnostics carry most of what a language service
would, and Monaco is a large dependency for the rest. Worth revisiting when a script gets long enough
that people want to navigate it rather than read it.
