# Phase 45 — Table mapping UX overhaul

**Status**: Done
**Plan reference**: `architecture/planning/done/table-mapping-ux-overhaul.md`

## What this covers

Related changes to how table mappings are created and edited, aimed at cutting the number of clicks and
decisions needed for the common case (map a table, accept sensible defaults) while keeping every default
overridable — plus two bug fixes surfaced while designing this:

1. Inferred mapping name, target-table autofill, and `.` allowed in names.
2. Inheritable "create target table if missing."
3. Editable target column name/type and transform, via a consistent show-value-plus-pencil interaction.
4. A mappings overview grid as the section's landing page, with single-request bulk mapping creation.
5. Fix: schema/table names combined into one string and split back apart — a real bug, not a hypothetical.
6. Fix: the auto-mapping editor silently showing the wrong source column for a row whose stored value
   isn't in the freshly loaded column list.
7. Fix: the Setup card's SQL previews don't use Monaco, and the generated `CREATE TABLE` isn't formatted.
8. A new inheritable "alter target table columns if missing or changed" setting.

## 1. Name inference and target autofill

**`TableMappingForm.tsx`**

- Track whether the operator has hand-edited the name (a `nameTouched` flag, set on the name input's
  `onChange`, the same "stop inferring once touched" shape used for schema-follows-table-pick in
  `MappingSide.setTableName`).
- While untouched, derive the name from the source table as `${source.schema}.${source.table}` whenever
  either changes.
- When the source table is set (chosen or inferred) and the target table is still empty, autofill
  `target.table` to the source's table name. This only *sets* an empty field — it does not override a
  target the operator already typed.
- Confirm no client or server validation currently rejects `.` in a mapping name (grounding: the input has
  no `pattern`, and `ConfigPaths.cs` writes the name straight into a filename, which is `.`-safe) — add a
  regression test rather than a code change, since nothing appears to need fixing here.

## 2. Inheritable provisioning

- **API/config**: `ReplicationTaskConfig` gains a `provisioning` field (shape TBD — likely mirrors
  `ProvisioningConfig`); `TableMappingConfig.provisioning.createTargetTableIfMissing` becomes nullable so
  absence means "inherit the replication's value." Resolution follows the same
  replication-then-mapping-overrides order phase 16 established for endpoints.
- **`ProvisioningCard.tsx`**: the checkbox becomes an `INHERITED`-badge-plus-override-toggle control, the
  same shape `MappingSide.tsx` already renders for connection/database. When not overriding, show the
  replication's resolved value read-only; toggling on turns it into an editable checkbox starting from
  the current effective value.
- **Overview page**: `OverviewPanel.tsx` (or a new card there) needs a place to set the replication-level
  default, parallel to how `EndpointsCard` sets the replication-level endpoints.

## 3. Column mapping editor

**`ColumnMappingEditor.tsx`**

- New small shared interaction: a value rendered as text with a pencil-icon button beside it; clicking
  the pencil swaps the text for an editable control (text input for name/transform, a type picker for
  type), with a way to commit or cancel. Worth factoring as its own component
  (`EditableValue`/`PencilField`, exact name TBD) since it's used three times in this one editor.
- **Target column name**: apply the pattern. `targetColumn` stays the current/desired name, but a rename
  now also appends to a new `renames: RenameStep[]` on the `ColumnMapping` — **an array, not a single
  "renamed from" value**, because the operator can rename a column more than once before provisioning
  ever applies anything, and provisioning needs the full history, not just the latest edit. Each step is
  `{ from: string; to: string; applied: bool }`. `applied` is what keeps a **swap** (A renamed to B and B
  renamed to A at the same time) from corrupting data — provisioning's `ALTER TABLE` planning has to
  reason about which steps already landed before deciding what to run next, rather than blindly applying
  every unapplied-looking step by current name. **The exact sequencing algorithm for a rename cycle is
  real design work, not settled here** — see Open questions.
- **Target column type**: new read path — call the existing `IProvisioner`/`CanonicalType` source→target
  type mapping (already used to build `CREATE TABLE`) to compute and show an inferred type for every row,
  even when nothing is customized. Apply the pencil pattern to let the operator override it. `ColumnMapping`
  needs a new optional field (e.g. `targetType: string | null`) that stays `null` for the common
  accept-the-inference case and is only set when the operator actually edits it — the inferred value
  itself is never written back into config.
- **Transform**: same pencil pattern, replacing the always-open `<input>` — collapses rows that don't use
  a transform instead of showing an empty text box in every one.
- Source column dropdown is unchanged.

## 4. Mappings overview grid

**New component**, reached via a special, **bolded "Overview" entry at the top of the existing sidebar**
in `TableMappingsPanel` — a sibling of the per-mapping list, not a replacement for it. The
sidebar-plus-pane shell stays; only the pane's content is new for this route.

- Lists every table in the replication's resolved default source database (reuses `useTables`, the same
  hook `MappingSide` already calls).
- A filter input (by name, likely schema too).
- Per row: table name, a mapping-count badge (0 for unused; the existing `useTableMappings` /
  per-mapping source lookups can compute this, or a new endpoint if doing it client-side means N+1
  requests — check before choosing).
- A checkbox per row plus a header select-all.
- **"Create N mappings"** button, enabled once at least one row is checked. **One API call**, not N —
  the server creates every selected mapping within a single request (name inferred per §1's rule, source
  set to that table, target autofilled to the same table name, everything else left unset to inherit).
  Because this can run long enough to want feedback, the endpoint reports progress as it goes — reusing
  the existing SignalR live-run infrastructure (`run-metrics.md` notes it's already there) rather than
  inventing a second push/poll mechanism just for this. The UI shows "created N of M" while the request
  is in flight, not a single opaque spinner.
- The existing sidebar-and-editor view for a single mapping stays reachable exactly as today (pick one,
  or `+` to hand-create one) — this is an additional landing page, not a replacement for the editor.

## 5. Fix: schema/table combined into one string, then split apart

Combining schema and table into one string is fine for a *label*; it is a bug the moment that string
becomes *data* someone parses back apart, because a schema or table name containing a literal `.`
(quoted identifiers allow this) breaks the split. Two confirmed instances:

- **`MappingSide.tsx`** — the non-`allowNewTable` (source) table `<select>` builds
  `${schema}.${table}` as each `<option>`'s `key`/`value` and the selection's current key, then recovers
  `schema`/`table` via `e.target.value.split('.')` in `onChange`. Fix: carry the selection as a real pair
  (e.g. key the `<option>` by index or by an opaque id, and look up `{schema, table}` from the selected
  table's own object) instead of a string that gets torn back apart.
- **`HookRenderer.QuoteMaybeQualified`** (`DataSync.Drivers.Generic/HookRenderer.cs`) — splits an
  operator-supplied hook-parameter value on `.` to tell a bare name from a schema-qualified one
  (`parts.Length == 2`). Breaks for a schema or table name that itself contains a `.`. This one is a
  harder fix: the value genuinely arrives as operator-typed free text (a hook parameter), so there's no
  structured pair to carry instead — needs its own look at whether the input can become structured
  (a schema field and a table field, rather than one string) or whether quoting/escaping rules need
  stating explicitly for the free-text case.
- **Audit the rest of the codebase** for the same shape before calling this done — these two were found
  by a direct search for `.split('.')`-style code; a combine-without-an-obvious-split (e.g. a dictionary
  keyed by a combined string, compared later) would not show up in that search and needs a separate look.

## 6. Fix: auto-mapping editor shows the wrong source column for stale rows

**Root cause**, in `ColumnMappingEditor.tsx`: each row's source-column `<select>` is
`value={m.sourceColumn}`. When the stored `sourceColumn` name isn't present in the just-loaded
`sourceColumns` list, the browser's native `<select>` silently renders the **first** `<option>` instead —
the component's actual state doesn't change, only the display does. This matches the reported symptom
exactly (rows "jump in" once metadata loads, all appearing to show the first source column) and explains
why **Auto-map "fixes" it**: it overwrites every row with a freshly computed valid value, masking the
display bug rather than encountering it.

Fix: stop relying on the browser's silent fallback. When a row's stored `sourceColumn` (or
`targetColumn`) doesn't match the currently loaded metadata, show that explicitly — an empty/placeholder
selection or a visible "unknown column" marker — rather than letting the select render as if the first
item were chosen. Left as-is, an operator can resave a mapping without ever noticing it silently changed.

## 7. Fix: Setup card SQL previews need Monaco, and CREATE TABLE needs formatting

`ProvisioningCard.tsx`'s `PlanPanel` renders plan SQL in a raw `<pre className="mono">` with an inline
`overflowX: 'auto'` that doesn't actually contain a long statement — it still widens the page and
produces a page-level horizontal scrollbar. `MappingPreview.tsx` already solved exactly this for the
"SQL this runs" screen by using `CodeEditor` (Monaco, `readOnly`) instead of a `<pre>`. Swap `PlanPanel`
to the same component, same props shape (`readOnly`, `language="sql"`).

Separately, `CreateTableStatement.Build` (`DataSync.Drivers.Generic/CreateTableStatement.cs`) joins every
column definition with `", "` onto one line:

```csharp
return $"CREATE TABLE {qualifiedTable} ({string.Join(", ", defs)}{primaryKeyClause});";
```

Change the join to a newline (plus indentation) between column definitions, so the generated DDL is
actually formatted, independent of whatever editor displays it:

```csharp
var body = string.Join(",\n    ", defs) + primaryKeyClause;
return $"CREATE TABLE {qualifiedTable} (\n    {body}\n);";
```

(exact formatting/indentation to taste during implementation — the point is newlines between columns,
not a specific style).

## 8. New setting: alter target table columns if missing or changed

Same inheritance shape as §2's `createTargetTableIfMissing`: a new field on both `ReplicationTaskConfig`
and `TableMappingConfig`'s provisioning block (`alterTargetTableColumnsIfMissingOrChanged` or similar),
nullable on the mapping so absence inherits the replication's default, same `INHERITED` badge/override
toggle in `ProvisioningCard.tsx`.

Unlike `createTargetTableIfMissing`, this is real schema evolution — the target table already exists.
Needs:

- **A new `IProvisioner` action** (`ProvisioningActions.AlterTargetTable` or similar) alongside the
  existing `EnableSourceChangeCapture`/`CreateTargetTable`.
- **A planner** that compares the mapping's configured columns against the target's actual catalog
  columns and emits `ALTER TABLE ADD COLUMN` for anything missing, and some form of type-change statement
  for anything that changed — additive-and-modifying only, never `DROP COLUMN`, matching
  `CreateTargetTable`'s own restraint (`ProvisioningConfig`'s doc comment: "additive only, never ALTER" —
  true only because nothing needed to be yet).
- **No new `PlanPanel`.** This stacks inside the existing **Target** plan panel, alongside the
  `CreateTargetTable` steps it already shows — a target's plan can carry both "create if missing" and
  "alter if present but out of shape" steps at once, gated independently by each setting's resolved
  (inherited-or-overridden) value. One `ProvisioningPlan` per side stays the shape; this action just adds
  more possible steps to the target side's plan, not a second side to show.

## What this phase does not build

- Any change to how a mapping actually *runs* (the replication pipeline itself) — §7 and §8 touch
  provisioning DDL generation specifically, not the pass that reads and writes rows.
- A general "editable field with pencil" component library beyond what these three column-editor uses
  need — scoped to this editor first.
- Dropping or destructively altering an existing target column — §8's `ALTER TABLE` planning is
  additive-and-modifying only, the same restraint `CreateTargetTable` already follows.

## How to verify when built

- Choosing a source table with no name yet typed produces `schema.table` as the mapping name; typing into
  the name field stops further inference.
- Choosing a source table autofills an empty target table field with the same table name; a target
  already typed is left alone.
- A mapping name containing `.` saves, loads, and round-trips through the sidebar link and route
  correctly.
- A replication with `createTargetTableIfMissing` set, and a mapping that does not override it, resolves
  to the replication's value; a mapping that does override wins locally.
- Target column name and type both show a pencil, default to sensible values (source name; inferred
  type), and only appear in saved config when actually edited.
- Transform uses the same pencil pattern and behaves identically to today once edited.
- The mappings overview grid lists all source tables, filters correctly, shows accurate per-table mapping
  counts, and "Create N mappings" produces exactly N new mappings with the expected defaults, via a
  single request that reports progress while it runs.
- The bolded Overview sidebar entry navigates to the grid and sits above the per-mapping list.
- Renaming a target column twice before saving/applying produces two ordered, unapplied `renames` steps;
  after Apply runs the ALTER, the corresponding step(s) are marked `applied`.
- `MappingSide`'s source table picker correctly selects a table whose schema or table name contains a
  literal `.` — regression test specifically for the bug this phase fixes.
- A mapping's row whose stored `sourceColumn` doesn't exist in freshly loaded source metadata renders as
  an explicit "unknown"/unselected state, never silently as the first column in the list.
- Setup card SQL previews render via `CodeEditor` with syntax highlighting, and a long generated statement
  stays contained inside its card — no page-level horizontal scrollbar.
- A generated `CREATE TABLE` with multiple columns renders one column definition per line.
- A replication with `alterTargetTableColumnsIfMissingOrChanged` set, and a mapping that does not override
  it, resolves to the replication's value; a mapping that does override wins locally. A target missing a
  mapped column, or with a mapped column whose type changed, produces non-destructive `ALTER TABLE` steps
  stacked into the existing Target plan panel (alongside any `CreateTargetTable` steps); an
  unmapped/removed column is never dropped.
- Full suite green, including updated Playwright screenshots for the new landing page and the changed
  column mapping editor.

## Open questions

- **The rename-cycle (swap) sequencing algorithm.** How provisioning safely applies a set of `renames`
  steps when two columns swap names, without double-applying or clobbering — needs real design attention,
  not just the `applied` flag's bookkeeping.
- Exact shape/name of the new `ReplicationTaskConfig.provisioning` field (now carrying two settings, not
  one).
- Whether reusing the SignalR live-run hub for bulk-create progress is a new hub or a channel on the
  existing one.
- Whether `HookRenderer.QuoteMaybeQualified`'s free-text schema-qualified value can become a structured
  input instead of operator-typed text that gets split — or whether it stays text with clearer quoting
  rules documented instead.
- The exact statement(s) a "changed" column type emits — a straight `ALTER COLUMN`/`MODIFY` differs enough
  per dialect, and some type changes are not safely alterable in place at all (needs a per-dialect
  `Unsupported` answer, the same way `CreateTargetTable` already reports one for an unmappable type).
- Whether other combine-then-split instances exist beyond the two found by direct search — needs a
  broader audit pass, not just the two confirmed spots.

---

# Retrospective

Eight items, all built. Three of the eight were fixes the plan had already diagnosed, and the phase
turned up four more bugs that were not in it — two of them years old.

## The plan was wrong about `.` in a name, and the test is why

§1 said to "confirm no client or server validation currently rejects `.`" and add a regression test
"rather than a code change, since nothing appears to need fixing here". `ConfigValidation.ValidateName`
rejected it outright. Writing the test first is what found that, in the ten seconds it took to run —
whereas the plan's grounding (the input has no `pattern`, and the name goes into a filename) was
checking the two places that happened to be fine.

Allowing `.` brought `..` and leading/trailing dots with it, since the name does become a path segment.
That is the cost of the feature, and it is paid in `ValidateName` rather than trusted to the filesystem.

## The settings govern the pass, not the person

Gating the Setup card's plan on `createTargetTableIfMissing` broke it, and correctly: the card started
saying "Target provisioning is off for this mapping" where it used to show the DDL. The settings answer
"what may a pass do unattended". The Setup card is where a person presses Apply deliberately, and
refusing to show them the statement because automation is off answers a question nobody asked. The gate
moved into `RunExecutor`, where the unattended pass actually is.

## A rename is a rename, and a swap is refused

The plan listed the swap-sequencing algorithm as open design work, and it stays open. Two columns
renamed past each other are reported as `Unsupported` naming both, because untangling one needs a
temporary name and an order that has to be right the first time — getting it wrong drops somebody's
data — and applying the two renames in sequence already works today. Inventing that unreviewed inside a
planner would have been the worst kind of progress.

What did get built is the part that is not ambiguous: an unapplied rename whose old name is still on the
target becomes one `RENAME`, a chain of them collapses to one statement, and the renamed column is then
treated as present so the ordinary pass does not plan an `ADD` beside it. Both wrong answers — an add
next to the old column, or a drop — leave a table that looks right in the catalog and is empty in the
column that holds the data.

`applied` is bookkeeping, not a latch. The planner reads the target's actual columns, so a flag left
stale plans nothing and a flag wrongly set stops nothing. It is marked after a fully successful apply
because a history that says a finished rename is still outstanding is a history nobody can read.

## The inferred type is shown and never stored

The canonical type system lives on the server, so the editor could not compute what a source column
becomes on the target — and a form showing the source's type beside an empty box asks the operator to
do that translation in their head. The new read path is the *same* translation the DDL uses, so what the
editor shows and what `CREATE TABLE` says cannot drift.

It stays a placeholder. Writing the inference into config would freeze today's answer against a source
column that later changes, which is the failure that looks like nothing at all until a pass writes the
wrong type.

## Two config types that saved and never loaded

`RenameStep` was written as a positional record and serialized perfectly well; YamlDotNet constructs
through a parameterless constructor and setters, so it threw on the way back in. An integration test
caught it within a minute.

Which was worth checking against phase 42's `ParameterCardinality` and `ParameterLayout` — same shape,
same fault. **A script declaring a vararg parameter could be saved and then never opened again.** Both
now round-trip, and `[DefaultValue]` on the bounds means a `Min` of `0` — the thing that makes a
parameter optional — is actually written down. That is the phase 46 `Enabled` bug for the third time,
and the round-trip test that would have caught all three is new.

## The combine-then-split audit found one in code written the same week

§5's two known instances were fixed. `HookRenderer.QuoteMaybeQualified` was the open one: the plan
preferred a structured schema-and-table pair, but a hook parameter's value is free text an operator
types, so there is no pair to carry. That rules the preferred fix out and leaves stating the rule.
`SqlDialect.SplitQualifiedName` splits on unquoted dots respecting the dialect's own quoting, so
`[dbo].[My.Table]` keeps its dot and a bare `My.Table` keeps the reading it has always had.

The broader audit the plan asked for found no remaining splits — and found the combine-*without*-a-split
shape it warned about, in the mappings overview written the day before: the selection set was keyed by
`${schema}.${table}`, so schema `dbo` + table `My.Table` and schema `dbo.My` + table `Table` were one
key and ticking one ticked the other. Keyed by a real pair now.

## The overview is the landing page because "first alphabetically" was never an answer

`/mappings` used to open whichever mapping sorted first, which beat an empty pane and lost to an answer.
The overview is equally right for a replication with forty mappings and one with none.

Bulk creation is one request for the reasons the plan gave, plus one it did not: a failure names the
table it failed on and reports what was created before it, which is the difference between "retry the
rest" and "work out what happened". A table that already has a mapping is a **skip**, not an error —
ticking every row on a replication that maps half of them means "map the rest".

## A long-standing Playwright flake, and the wrong bug it nearly caused

Test 06 asserted a triggered run read 2 rows. A replication that has never run is due immediately, so
the scheduler could take the pass first and the triggered run then correctly read nothing. Touching the
rows first narrowed the window without closing it — the scheduler can still slip in between the UPDATE
and the click — so the test now repeats the whole gesture when a run reads nothing, which is the only
honest way to assert something about a run rather than about who won a race.

Worth recording: when this flake fired mid-suite it diverged the serial suite's state and surfaced as an
unrelated-looking failure several tests later. That is a good argument for reading a serial suite's
first failure rather than its loudest one.

## Verification

- `NameValidationTests` (17) — `.` allowed, `..` and leading/trailing dots rejected, plus the existing
  rules unchanged.
- `ProvisioningResolutionTests` (5) — each setting falling back independently, and where a resolved
  value came from.
- `AlterTargetTablePlannerTests` (17) — additive and modifying only, never `DROP`; a rename planned as
  one statement, a chain collapsed, a rename already reflected on the target planning nothing, a swap
  and a rename-onto-an-occupied-name both refused by name; a chosen target type used verbatim including
  where no canonical mapping exists.
- `MsSqlRenameColumnTests` (2) / `PostgresRenameColumnTests` (1) — `sp_rename`'s qualified-old,
  bare-new argument shape and its literal escaping; Postgres keeping the ANSI spelling.
- `QualifiedNameSplitTests` (7) — quoting respected, escaped closing brackets, and the ambiguous
  unquoted case keeping its historical reading.
- `ScriptParameterYamlRoundTripTests` (2) — a vararg parameter surviving a save and a load, and a plain
  one staying plain.
- `TableMappingsControllerTests` bulk region (6) — one mapping per table named after it, everything else
  left inherited, existing mappings skipped rather than failing, a duplicate within one batch created
  once, and the empty and missing-replication cases.
- `PreviewIntegrationTests` additions (2) — a target column renamed against a real database keeping its
  rows, the plan going quiet afterwards and the step marked applied; and inferred types answering every
  source column with the same translation the DDL uses.
- Playwright 26–30 — a dotted table name end to end, an unknown column shown as itself, Monaco DDL that
  stays inside its card, name and target inference stopping once touched, provisioning inherited and
  overridden, the pencil for type and rename with the resulting plan, and the overview's counts,
  filtered select-all and single-request creation.
- Playwright 15 updated: `/mappings` lands on the overview now. Test 06 made deterministic.
- Full .NET suite green: 701 tests. Playwright: 32 green. `tsc -b` clean, `oxlint` unchanged at four.

## Open questions

- ~~**The rename-cycle (swap) sequencing algorithm.**~~ Still open, deliberately — but no longer
  dangerous: a swap is refused by name rather than half-applied. Worth a phase of its own if anyone
  ever needs it.
- ~~**Shape of `ReplicationTaskConfig.provisioning`.**~~ The same `ProvisioningConfig` as the mapping's,
  nullable at both levels, resolved by `ProvisioningResolution` — one type asked at two levels, the
  shape phase 16 established for endpoints.
- ~~**A new hub or a channel on the existing one.**~~ A channel: `bulkMappingProgress` on `RunHub`,
  under a batch id the client owns and joins before the request leaves.
- ~~**Whether `QuoteMaybeQualified`'s value can become structured.**~~ It cannot — it is operator-typed
  free text. The quoting rule is stated and honoured instead.
- ~~**The statement a changed column type emits.**~~ `RenderAlterColumnType`, virtual with an ANSI
  default and a SQL Server override, returning null for an engine that cannot express the change — in
  which case the plan warns and names the column rather than emitting something that might truncate.
- ~~**Whether other combine-then-split instances exist.**~~ None remaining; the audit found one
  combine-without-a-split instead, since fixed. `WatermarkKey`'s collision stays documented and
  unchanged: changing the format would orphan every stored watermark and silently resync every
  replication.
