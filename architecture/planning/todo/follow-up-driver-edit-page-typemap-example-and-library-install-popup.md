# Follow-up: two gaps on the new-driver screen — a thin typeMap example, and the library picker eating the page

**Status, 2026-09-22**: both fixed. #2 shipped with one real correction from this doc's own plan — see its
own note below: auto-closing the popup on install, as originally proposed here, turned out to be a real bug.

Found reading `DriverEditPage`/`driverYamlAssembly.ts` after `driver-yaml-authoring-ui.md`'s core shipped.
Both frontend-only.

## 1. `RAW_BODY_SKELETON`'s typeMap is one entry — not enough to actually start from — fixed

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

**Fixed**: `RAW_BODY_SKELETON` now carries the full mysql example verbatim, its `dialect` block given the
same commented-options treatment, plus a trailing commented block naming the six kinds the example doesn't
use (`Boolean`, `Float`, `Time`, `TimestampTz`, `Guid`, `Xml`) so all 17 are represented, shown or named.
Verified end to end, not just read back: `driver-authoring.spec.ts` creates a real driver through the form
with this exact skeleton untouched, and the server-side `YamlDotNet` parse + `BuildDriver` round-trip both
succeed — proof the comment styling and the extra kinds don't trip the real parser, not just a visual check.

## 2. The library-install panel is always rendered, full size, whether or not it's needed — fixed

`DriverEditPage`'s ADO.NET branch (`DriverEditPage.tsx:147-164`) renders the "Library" `<select>` of
already-installed libraries, then unconditionally renders the full `LibraryFindPanel` right below it —
same component `LibrariesPage` uses as its whole page's content (chips sidebar, NuGet search box, results
list, install command). On the authoring form it's just one more field among many, but it takes the same
amount of vertical space it takes as a dedicated page's entire body — for the common case (the library the
operator wants is already installed and just needs picking from the dropdown), that space is spent on
something never touched.

**Fixed**: a button — "Install a new library…" beside the `<select>` — opens `InstallLibraryDialog`, which
wraps the existing `LibraryFindPanel` in `.modal-backdrop`/`.modal`, the same shell
`LibraryFindPanel.tsx`'s own `TrustInstallDialog` already built. `LibraryFindPanel` itself is unchanged.

**A real bug in this doc's own plan, found by the Playwright spec, not by reading**: this doc originally
proposed `onInstalled` both selecting the library *and* closing the popup in the same call
(`setForm(...); closePopup()`) — built that way first, and `driver-authoring.spec.ts` immediately hung
waiting for the "Installed" confirmation text that `LibraryFindPanel` shows on success. Root cause: closing
the popup unmounts `LibraryFindPanel` (and the "Installed" text with it) in the *same* render pass as the
success state that would have shown it — React batches both `setState` calls, so the confirmation never
paints at all. The server-side install itself was fine throughout (confirmed via the webServer's own
request log — `POST /api/libraries` returning `200` in ~1.3s every time); nothing wrong until the UI
discarded its own success state before rendering it. Fixed by *not* auto-closing: `onInstalled` selects the
library into the form immediately, but the dialog stays open until the operator closes it themselves,
same as every other modal in this app — they get to see "Installed" (and, for a non-curated pick, the
detected factory type) before it goes away.

**Scoped to `DriverEditPage` only** — `LibrariesPage`'s own embedding is unchanged: that page's entire
purpose *is* finding and installing a library, so keeping it inline there is correct; the popup is only for
the case where installing one is a small step inside a larger form that's usually not needed at all.

## Where this applies

Both are `DriverEditPage`-only changes. `driverYamlAssembly.test.ts` references `RAW_BODY_SKELETON` by
symbol, not by hardcoded text, so it needed no changes for #1's content swap.
`driver-authoring.spec.ts` needed real updates for #2's gating (open the popup before the quick-add chip
is reachable; close it explicitly after seeing "Installed", instead of asserting it vanished on its own).
