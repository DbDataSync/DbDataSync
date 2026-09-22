# Phase 165 — Config writes that missed their own key, and two screens that looked done (done)

Small, unplanned, and reported from use: three things noticed on the Admin screens. One is a real defect
in `DbDataSyncConfigFile.SetValue`; the other two are follow-ups to
`b555c21 Admin console: capability pills, mode toggles, and outbound links`, which had **already made
both changes** and had each land slightly short of what the screen needed. No `todo/` doc preceded this —
it is written up because the defect is worth a reader's time, not because the work needed designing.

## Why

Reported, in the reporter's own words:

> the grouping in the admin config isn't very visible, they should each have thair own table. The list
> of well known libraries for the is not off to the side, it is stacked on top. Trying to disable the
> Auth:Network:Admin setting didn' actually change anything

All three are against a build that already has b555c21. That matters for reading the first two: they are
not "this was never done", they are "this was done and it does not land".

## The defect: a key can be in the file twice, under two different section headers

`dbdatasync.config.yaml` is read by flattening it to ASP.NET Core's `Section:Key` shape, so these two
files are **identical** to `DbDataSyncConfigFile.Read` and to every consumer downstream of it:

```yaml
DbDataSync:                              DbDataSync:Auth:Network:
  Auth:Network:Admin: "loopback"           Admin: "loopback"
```

They are not identical to `SetValue`, which took the split as given: it looked for a line reading
exactly `<section>:`, then for a `  <key>:` line under it. Both shapes are in active use, by different
writers, and have been since phase 164 regrouped the key surface:

| writer | section | key |
| --- | --- | --- |
| `SetupSteps.ApplyAuthentication` | `DbDataSync:Auth:Network` | `Admin` |
| `LegacyConfigMigration` (`Auth:Disabled` → here) | `DbDataSync:Auth:Network` | `Admin` |
| `AdminConfigService.Set` (the Admin screen) | `DbDataSync` | `Auth:Network:Admin` |
| `ConfigValueCommand` (`dbdatasync config set`) | `DbDataSync` | `Auth:Network:Admin` |

So on any install whose auth was configured by `setup` or carried through the phase 164 migration — which
is every install that has `Auth:Network:Admin` set at all, since nothing else puts it there — the Admin
screen's write found no matching line, took the "not there yet" branch, and **inserted a second one**
under its own `DbDataSync:` header. The file then said two contradictory things about one key, and `Read`
resolved it by document order: last one wins. `setup`'s block goes at the end of the file; the Admin
screen inserts directly after the `DbDataSync:` header, which is near the top. The old value won.

Nothing failed. The save returned 200, the commit landed, the restart banner appeared — and the screen,
re-reading the file, showed the old value back again, because that is genuinely what the file now meant.
Restarting changed nothing either. "Didn't actually change anything" is exactly right, and it applied to
every key `setup` had written, not only this one.

### The fix

`SetValue` now finds the key the way `Read` does — by its flattened name — and only falls back to the
caller's `section`/`key` split to decide where to *create* a key the file does not have yet. The new
`FindKeyLines` walks the file keeping an indent stack, so a key written as nested mappings (`Auth:` →
`Network:` → `Admin:`, which a human editing this file by hand would naturally write) matches as readily
as either machine-written shape. `RemoveValue` uses the same lookup, for the same reason: the migration's
"write the new key, drop the old one" pair could otherwise half-apply.

Where a file already has the duplicate an older build wrote, **every** copy is set rather than the extras
pruned — the file then says one thing however the reader resolves it, without this code deleting a line
somebody may have written a comment around.

## A second, quieter regression found on the way

`SetValue`'s guard against writing a credential into this git-tracked file compared its bare `key`
argument to the literal `"StateConnectionString"`. Phase 164 renamed that key to `State:ConnectionString`
and moved the split; every caller has passed something else ever since. **The guard has matched nothing at
all since phase 164** — `dbdatasync config set State:ConnectionString "...;Password=..."` and the Admin
screen's own field would both have written the password to disk and committed it. It now matches on the
flattened key, and accepts either the phase 164 name or the pre-164 one. The existing test passed
throughout, because it was written against the pre-164 key name and so exercised the one spelling nothing
produces any more.

## The two screens b555c21 had already changed

**Admin → Configuration.** b555c21 added `groupByFirstFragment` and a `.grid-group-head` band between
groups, inside one card. The rows were grouped; the page still read as one long table, which is the thing
that made "where are the auth settings" a matter of reading every key. Each group is now its own card,
with the group's name and its `DbDataSync:<group>:*` prefix in a card header and its own column header
row — so a group scrolled to in isolation still says which column is which. b555c21's grouping pass,
`ModeToggle`, `allowedValues` and Source-before-Value column order are all kept as they were; only the
container changed. `.grid-group-head` is deleted with its last caller.

**Admin → Libraries.** b555c21 moved the well-known-library shortcuts into a sidebar beside the search
box — and it rendered stacked above it anyway. The cause is one missing property:

```jsx
<div className="card-body" style={{ display: 'flex', gap: 20, alignItems: 'flex-start' }}>
```

`.card-body`'s own rule is `display: flex; flex-direction: column; gap: 11px`. The inline style overrides
`display` and `gap` and says nothing about `flexDirection`, so the column direction survives and the two
children stack — the sidebar first, which is exactly "not off to the side, it is stacked on top". (The
same file's *other* `card-body` at `TrustInstallDialog` does write `flexDirection: 'column'` explicitly,
which is why only this one went wrong.) Replaced with `.find-layout`/`.find-main`/`.find-aside` classes,
keeping the aside on the left where b555c21 put it, and stacking deliberately below 800px, where a 180px
column beside an input leaves neither usable.

## What this does not do

- Does not change how `dbdatasync.config.yaml` is *created* — a new key still goes in the flat two-level
  shape the caller asks for. Only finding an existing one got smarter.
- Does not prune the duplicate line an older build may have left in a file (see above).
- Does not remove a section header left empty by `RemoveValue`, which it never did.
- Does not touch `SetListValue`/`RemoveListValue`, which need block extents rather than a key line.
- Does not stop an admin editing a key something else overrides (below). The file is still worth getting
  right, and a deployment can drop the environment variable later; refusing the edit would be a guess at
  which of the two the operator means to win.

## The third thing with the same symptom: a save that could never take effect

Written up here as a follow-up because it was found while fixing the first, and reported as not done
before it was done.

`AdminConfigService.ToEntry` reports `source: "file"` whenever the file carries the key — deliberately,
and for a good reason its own comment gives: the `IConfiguration` provider list is frozen at startup, so
inferring the source from it would leave a key this screen just wrote looking permanently un-adopted. But
`DbDataSyncHost.InsertConfigFile` inserts the file provider *immediately before* the environment one, so
the environment and the command line both outrank the file. A deployment setting
`DbDataSync__Auth__Network__Admin` in its environment would see a save here write the file, the value come
back changed, the restart banner appear — and the running process go on using the environment variable,
across that restart and every one after it, with nothing on the screen ever saying why. The same "didn't
actually change anything" as the file-writer bug, reached by a different route.

`AdminConfigEntry` gains `OverriddenBy` and `OverriddenValue`, and the Source column shows an
`overridden by environment variable` pill whose title names the winning value and what to do about it.
`Source` still reads `file`, because the file is what an edit here writes; the pill answers whether that
edit will take.

The check is **positional, not "who wins overall"**: `OutranksTheFile` takes the environment provider's
index as the boundary and asks whether anything from there on supplies the key. Asking
`IConfigurationRoot` who wins would be wrong twice — the file provider's snapshot is frozen at startup
and will not have a key written a moment ago, and it is absent entirely from a process that started
before the file existed. Neither changes who would outrank the file on the next start, which is the
question worth answering. Reported only for a key the file actually carries: for any other, `Source`
already names whatever is winning, and a pill on every default-valued row would be noise.

`QuickAddChip` also comes off `.btn-link.quiet`, whose hover colour is the destructive red — wrong for a
"+ add" control, and much more visible now they are a vertical list. The `.quiet` variant itself is left
alone: 25 other callers use it, and most of them really are destructive.

## Progress

- `DbDataSyncConfigFile.SetValue`/`RemoveValue` rewritten around `FindKeyLines`; credential guard matched
  on the flattened key.
- `AdminConfigService`: `OutranksTheFile`, and `OverriddenBy`/`OverriddenValue` on `AdminConfigEntry`.
- `AdminConfigPage.tsx`: `GROUP_NAMES` and a `label` on b555c21's grouping helper; one card per group;
  the `.override-pill` in the Source cell. Test IDs are unchanged, `admin-config-group-<group>` included
  — it moves from the band to the card — with `admin-config-overridden-<key>` added.
- `AdminLibrariesPage.tsx`: the find panel's inline layout styles replaced by classes; `QuickAddChip` off
  `.quiet`. `admin-libraries-known-sidebar` is unchanged, and b555c21's nuget.org link is untouched.
- `index.css`: `.config-groups`, `.override-pill`, `.find-layout`/`.find-main`/`.find-aside`;
  `.grid-group-head` removed.
- Six new tests in `DbDataSyncConfigFileTests`: both section-split directions, the nested-mapping shape, a
  value containing a colon (the key-boundary scan has to stop before a URL's `//`), the credential guard
  under both of its names, and `RemoveValue` across a split. Three in `AdminConfigServiceTests` for the
  override reporting, including the two negative cases — nothing outranking, and a key the file does not
  carry.

## Outcome

Pending — see the commit that moves this line.
