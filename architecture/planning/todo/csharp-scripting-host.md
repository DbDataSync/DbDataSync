# Embedding C# as a scripting language — the host

**Status: proposal, not agreed.** Written to the brief "embed C# as a scripting language that can be
configured at a per connection, per replication task, or per table mapping level". This doc covers the
*host*: how a script is stored, compiled, resolved and run. The extension points it plugs into are
`csharp-script-extension-points.md`; change-tracking query generation is
`script-generated-change-queries.md`.

## What this is for

Five things were asked for, in the order they were asked:

1. custom source query transforms — generating column expressions, up to rebuilding the whole query
   from metadata
2. custom data transforms
3. custom metadata providers
4. (eventually) custom target statement generation
5. source change-tracking query generation, especially for ODBC and JDBC

All five are the same shape: **a place where DataSync generates something, and an operator knows
better than we can.** Which is why the host is one thing and the extension points are many.

## Where scripts live

The config store is git-backed with an auto-commit per change (phase 1) and a history UI already
built (phase 6, the Version Control tab). Scripts belong there — an operator changing a transform gets
diffing, blame and revert for free, and gets them through the machinery that already exists rather
than a second one.

```
config/scripts/<name>.cs        the code
config/scripts/<name>.yaml      the manifest
```

Flat, mirroring `config/connections/<name>.yaml`. Code in its own file rather than embedded in the
YAML, because code embedded in YAML diffs badly, cannot be opened by an editor, and re-indents on
every round trip through a serializer.

The manifest:

```yaml
name: uppercase-region
kind: RowTransform            # which extension point this implements
entryType: UppercaseRegion    # the type in script.cs implementing the contract
description: Normalises Region to upper case before staging.
parameters:                   # declared here, supplied at each binding site
  - name: columnName
    type: string
    required: true
capabilities:                 # only for slots where capability discovery needs an answer
  detectsDeletes: false
enabled: true
```

**`capabilities` is not optional decoration.** The SPA's Kind pickers are driven entirely by
`DriverCapabilities`, and phases 17–19 made a point of never string-matching a Kind to infer
behaviour. A scripted reader's `DetectsDeletes` and a scripted writer's `SupportsReconciliation`
cannot be inferred from an interface — the script either reports deletes or it doesn't, and only its
author knows. So they are declared, and the UI reads them exactly as it reads a driver's.

## Compilation: Roslyn to an assembly, not Roslyn scripting

Three candidates:

| | what | why not |
| --- | --- | --- |
| `Microsoft.CodeAnalysis.CSharp.Scripting` | script fragments with a `globals` object | Same Roslyn dependency, less control over the reference set, and the only thing it buys is not writing a class declaration |
| `CSharpCompilation` → `AssemblyLoadContext` | compile a normal C# file, load the assembly | **recommended** |
| precompiled plugin DLLs | operator builds a project, drops the DLL in | loses "registered in the app and edited there", which is most of the ask |

**Recommendation: `CSharpCompilation` into a collectible `AssemblyLoadContext`.** Full C# semantics
matter here — "rebuild the entire query from metadata" is real code with types and helper methods, not
an expression. Requiring `public sealed class X : IRowTransform` is a feature, not overhead: it makes
the contract explicit, gives the operator's editor something to work with, and makes a compile error
land on the thing that is actually wrong.

Open question: whether to add a **template wrapper** for one-liners, where the operator writes a method
body and we wrap it in a class before compiling. That keeps the scripting-engine ergonomics without a
second engine. Worth doing only if writing the class declaration turns out to actually annoy anyone —
deciding now would be inventing a requirement.

### Compile at save, not at first run

Phase 16 established the rule: a mapping that cannot run is rejected while the operator is still
looking at it. Same here. `POST /api/scripts/{name}` compiles, and a compile error is a 400 carrying
the Roslyn diagnostics with line and column. A script that will not compile never reaches a run.

### The compiled-assembly cache belongs on disk, not in memory

This is the non-obvious one. **The TaskRunner is a process spawned per replication run.** An in-memory
compile cache therefore buys nothing at all — every run starts cold, and Roslyn's first compilation in
a process costs on the order of a second before it compiles anything of ours. On a 15-second schedule
that is most of the interval spent starting a compiler.

So: compile once, key by the SHA-256 of the source plus the reference set, and write the assembly to a
cache directory beside the state database (not git-tracked, safe to delete). A run loads a DLL. The API
populates the cache when it validates on save, so in the normal case a run never compiles at all.

The in-memory cache still matters in the API, which is long-lived and compiles on every save and
preview.

## The reference set is a guardrail, not a sandbox

Scripts get an explicit, closed set of assembly references: `System.Runtime`, `System.Linq`,
`System.Collections`, `System.Text.RegularExpressions`, `System.Globalization`, and
`DataSync.Scripting.Abstractions`. Not `System.Net.Http`, not `System.Diagnostics.Process`, not
`System.IO`.

**This is not a security boundary and must not be described as one.** A script that wants
`System.IO` can reach it through reflection, and .NET has had no in-process code-trust mechanism since
CAS was removed. Claiming a sandbox we do not have would be worse than having none, because someone
would rely on it.

The honest threat model: **this is an admin-authored extension point.** Anyone who can register a
script can already write a connection config pointing at any database, and can already read every
secret the process can. The risks worth engineering against are *accident* and *supply chain*, not a
hostile author:

- the closed reference set makes "I'll just call out to an HTTP API from inside a row transform" fail
  at compile time, where it is a design conversation rather than a production incident
- every script is git-tracked with an author and a diff — that is the audit trail
- scripts execute in the **TaskRunner**, which is already a separate short-lived process per run, so a
  script that hangs or crashes takes down one run and not the API

That last point has one genuine hole: **metadata providers and query previews have to run in the API**,
because that is where the SPA's pickers ask. A metadata provider script therefore *can* hang or crash
the API. Options: run them with a hard timeout and accept the risk; or run them in a short-lived child
process the way runs already are. The second is more work and more correct. Not decided here.

## Resolution: connection → replication → table mapping

The hierarchy asked for is mapping highest, then replication, then connection. This is the same
problem phase 16 solved for endpoints, and it should reuse that shape — `ScriptResolution` alongside
`EndpointResolution`, resolving before anything is executed, so that no reader, writer or transform
ever has to know inheritance exists.

Each of the three config objects gains:

```csharp
public Dictionary<string, ScriptBinding?> Scripts { get; set; } = new();
```

keyed by slot name (`rowTransform`, `sourceQuery`, `metadataProvider`, …).

```csharp
public sealed class ScriptBinding
{
    public required string ScriptName { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
}
```

### A dictionary, because "explicitly none" has to be sayable

If a connection binds a row transform and one mapping must not use it, the mapping needs to say so.
"Absent" cannot mean both *inherit* and *none*. A dictionary distinguishes them: the key being absent
means inherit; the key present with a null value means override with nothing.

```yaml
scripts:
  rowTransform: ~     # explicitly none — overrides whatever the replication or connection binds
```

versus no `scripts` key at all, which inherits. This is the one place the phase 16 model does not
carry over directly — endpoints had no "explicitly nothing" state, because an endpoint is always
required.

### The binding is atomic per slot

Phase 16 resolves *fields* independently, so a mapping can override a database while inheriting a
connection. Scripts should not: the binding — script name and parameters together — is replaced whole
by the most specific level that sets it.

The reason is legibility. If parameters merged across levels, then reading a mapping would not tell you
what runs; you would have to read three files and merge them in your head. A half-inherited binding
("the replication's script, with the connection's parameters") is almost always a mistake, and the
config that expresses it looks identical to the config that meant it.

Noted as an open question: an explicit `inheritParameters: true` opt-in would give the merge back for
anyone who wants it, without making it the default. Nobody has asked for it yet.

### Cardinality is a property of the slot

Some slots compose and some cannot:

- **`rowTransform`, `columnExpression`** — a list. Chaining two transforms is natural.
- **`metadataProvider`, `sourceQuery`, `targetStatement`, `changeQuery`** — exactly one. Two
  implementations of "build the query" cannot both win.

For list slots the override still **replaces** rather than appends, for the same legibility reason. An
operator who wants the connection's transform *and* one more can restate both on the mapping, and then
the mapping says what it does.

## Project layout

Mirroring `Drivers.Abstractions` / `Drivers.*`:

- **`DataSync.Scripting.Abstractions`** — the contracts an operator implements, plus the context types
  handed to them. Tiny, no Roslyn. Referenced by the drivers (they consume scripts) and by scripts
  themselves.
- **`DataSync.Scripting`** — the host: compiler, cache, loader, `ScriptResolution`. References Roslyn.
  Referenced by the API (validate, preview, metadata) and the TaskRunner (execute). **Not** referenced
  by drivers — a driver takes an already-resolved delegate, never a compiler.

That split is what keeps Roslyn out of the driver projects, which matters because a driver is meant to
be a small self-contained thing.

## SPA

- A **Scripts** section beside Connections: list, editor, compile status, and a **Test** action that
  runs the script against sample input and shows the result. The connection-test flow from phase 19 is
  the precedent — an operator gets to find out it works before a run does.
- **Binding pickers** on the connection, replication and mapping screens, with the `INHERITED` badge
  and *Override for this table* toggle phase 16 built for endpoints. This is the same interaction with
  a different payload, and it should look identical.
- Editor: a plain monospace textarea is enough to start. Monaco is a large dependency to take on for a
  first cut, and the compile-on-save diagnostics carry most of the value a language service would.

## Suggested phasing

Each of these is a phase doc's worth of work; none of them is useful without the one before it.

| | what | why here |
| --- | --- | --- |
| A | Host, registry, manifest, compile-on-save, disk cache, `ScriptResolution`, **one slot: `rowTransform`** | Proves the whole spine end to end against the simplest contract |
| B | `metadataProvider` | The first slot that runs in the API, so it forces the isolation question above |
| C | `columnExpression`, then `sourceQueryBuilder` | The source-query asks, smallest first |
| D | `changeQuery` | Folds into the change-tracking work; see `script-generated-change-queries.md` |
| E | `targetStatement` | Explicitly "eventually" in the brief, and the riskiest — a wrong writer loses data |

## Open questions

- **Metadata-provider isolation.** Accept the API risk, or spawn a child process? The second is
  correct and is more work than the rest of slot B.
- **Timeouts.** A `CancellationToken` is honoured only by a cooperative script. `while(true){}` needs a
  process boundary to kill. Same answer as above, and it is the same question.
- **Per-cell cost.** Phase 14 measured what the in-memory row shape costs; a per-cell delegate over
  millions of rows is the same class of question and the same benchmark tool answers it
  (`tools/benchmarks`). Measure before choosing per-column over per-row, not after.
- **`ColumnMapping.Transform` already exists and is never read.** Declared in phase 1 and dangling
  since. It should either become the `columnExpression` binding or be deleted; leaving a field that
  looks like it does something is worse than either.
