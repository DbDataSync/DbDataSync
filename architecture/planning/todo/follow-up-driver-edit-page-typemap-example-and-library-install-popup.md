# Follow-up: two gaps on the new-driver screen — a thin typeMap example, and the library picker eating the page

Found reading `DriverEditPage`/`driverYamlAssembly.ts` after `driver-yaml-authoring-ui.md`'s core shipped.
Both frontend-only. Documented to fix later, not fixed here.

## 1. `RAW_BODY_SKELETON`'s typeMap is one entry — not enough to actually start from

A brand-new driver's raw dialect/typeMap editor starts from `RAW_BODY_SKELETON`
(`driverYamlAssembly.ts:95-100`):

```yaml
dialect:
  quoteIdentifier: doubleQuote
  parameterPrefix: "@"
  rowLimit: limitOffset
typeMap:
  int: Int32
```

One `typeMap` entry. `driver-yaml-authoring-ui.md` itself already flagged structured `typeMap` authoring
as out of scope for v1 and this raw editor as the deliberate escape hatch — but the escape hatch's own
starting point barely shows what the DSL can express. An operator with no existing `driver.yaml` to copy
from has to already know `TypeMapEntryYaml`'s `(p,s)`-placeholder syntax and the shape of a `{ kind, ... }`
object entry to write anything past integers — exactly the "genuinely harder than everything else in this
form" DSL that doc's own §5 describes, with no worked example of the hard part.

**A real, complete example already exists in this repo** —
`src/DbDataSync.Drivers.Descriptor/Resources/mysql.generic.driver.yaml`, the bundled catalog descriptor:

```yaml
typeMap:
  tinyint:        Int8
  smallint:       Int16
  int:            Int32
  bigint:         Int64
  "decimal(p,s)": { kind: Decimal, precision: p, scale: s }
  double:         Double
  "varchar(n)":   { kind: String, length: n, unicode: true }
  text:           { kind: String, max: true }
  datetime:       Timestamp
  date:           Date
  json:           Json
  blob:           { kind: Binary, max: true }
```

Twelve entries covering every shape the DSL actually has: a bare scalar mapping, the `(p,s)`-placeholder
substitution form, a `{ kind, length, unicode }` object, and a `{ kind, max }` object — plus its own
`dialect:` block already models the "comment out the alternative, here's what else this field accepts"
style (`# backtick | doubleQuote | bracket`) the current skeleton's `dialect` block doesn't use at all.

**Not fully exhaustive either, worth noting for whoever picks this up**: `CanonicalTypeKind`
(`src/DbDataSync.Core/Sql/CanonicalType.cs:16-41`) has 17 real kinds; the mysql example demonstrates 10
(`Int8/16/32/64`, `Decimal`, `Double`, `String` ×2 shapes, `Timestamp`, `Date`, `Json`, `Binary`) and
leaves out `Boolean`, `Float`, `Time`, `TimestampTz`, `Guid`, `Xml`. Swapping the skeleton for the mysql
example wholesale is already a large improvement over one entry; adding the missing kinds as commented-out
extra lines (matching the `dialect:` block's own commenting style) would make it a genuinely complete
reference rather than "one real driver's own subset."

**Fix**: replace `RAW_BODY_SKELETON`'s `typeMap` (and give its `dialect` block the same commented-options
treatment `mysql.generic.driver.yaml` already uses) with this fuller example — either lifted directly from
the mysql resource file, or written fresh with all 17 kinds represented. A vendor-specific driver being
authored will still need to edit the native type names on the left of every entry; the point is giving the
operator a real, complete shape to edit rather than a single line to extrapolate the whole DSL from.

## 2. The library-install panel is always rendered, full size, whether or not it's needed

`DriverEditPage`'s ADO.NET branch (`DriverEditPage.tsx:147-164`) renders the "Library" `<select>` of
already-installed libraries, then unconditionally renders the full `LibraryFindPanel` right below it —
same component `LibrariesPage` uses as its whole page's content (chips sidebar, NuGet search box, results
list, install command). On the authoring form it's just one more field among many, but it takes the same
amount of vertical space it takes as a dedicated page's entire body — for the common case (the library the
operator wants is already installed and just needs picking from the dropdown), that space is spent on
something never touched.

**Fix**: gate it behind a button — "Install a new library…" beside/below the `<select>` — that opens
`LibraryFindPanel` in a popup instead of inline. No new modal machinery to build: `.modal-backdrop`/
`.modal` are already real, styled, and used in this exact file's own dependency
(`LibraryFindPanel.tsx`'s own `TrustInstallDialog`, `index.css` modal rules) — wrapping the existing
`LibraryFindPanel` render in that same shell is the same pattern one level up, not a new one. `onInstalled`
already exists as the exact callback that would also close the popup (`setForm({ ...form, library:
installedId }); closePopup()`), so the component itself needs no change, only how `DriverEditPage`
mounts it.

**Scoped to `DriverEditPage` only** — `LibrariesPage`'s own embedding is unchanged: that page's entire
purpose *is* finding and installing a library, so keeping it inline there is correct; the popup is only for
the case where installing one is a small step inside a larger form that's usually not needed at all.

## Where this applies

Both are `DriverEditPage`-only changes; `driverYamlAssembly.ts`'s skeleton (item 1) is also read by the
Playwright/unit tests that assert on its shape (`driverYamlAssembly.test.ts`,
`driver-authoring.spec.ts`) — worth checking those don't assert on the exact current skeleton text before
replacing it.
