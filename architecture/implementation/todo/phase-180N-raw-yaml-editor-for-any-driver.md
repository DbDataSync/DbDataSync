# Phase 180N: a completely raw YAML editor mode for any driver definition

**Status: done (2026-09-23).** Third of five phases for the JDBC/driver-editing UI round requested
2026-09-23.

## The gap

`DriverEditPage`'s structured form already has a partial escape hatch — `form.rawBody` holds
dialect/typeMap/metadataQueries verbatim (`driverYamlAssembly.ts`'s own doc comment: "the raw editor is
always there as the escape hatch if a load doesn't look right"). But that's only a hatch for the fields
`STRUCTURED_KEYS` doesn't cover. A driver.yaml that needs something the structured fields *do* own edited
in a way the form can't express — a comment an operator wants preserved anywhere outside `rawBody`, a key
ordering, a `library:` value the ADO.NET picker's `<select>` doesn't offer because the library isn't
installed yet but the operator knows it will be — has no path through this screen at all today; only
direct file editing outside the console.

`parseDriverYaml`'s own doc comment already admits the ceiling: "**Not a YAML parser.** ... A hand-authored
file that puts a top-level key's first value on the same line with unusual spacing, or otherwise deviates
from that shape, may not split cleanly." A driver.yaml written by hand, or by a future tool, that doesn't
match this app's own generator shape can silently mis-load into the structured form today — wrong values
in fields, not an error. A full raw mode is also the honest answer to *that* gap: an operator who
suspects the structured split misparsed their file needs a way to see and edit the literal bytes, not
just trust the split.

## What ships

A mode toggle at the top of the form, next to the page title: **Structured** / **Raw YAML** (two buttons
or a small segmented control, `driver-edit-mode-structured` / `driver-edit-mode-raw`). Raw mode replaces
the entire card body (id/displayName/base/library/jdbc/pipeline-phases/rawBody) with one full-height
`CodeEditor` bound to the complete YAML text, save button unchanged (posts the literal text as `body.Yaml`
to `Create`/`UpdateYaml` exactly as today, no `assembleDriverYaml` involved in raw mode).

**Switching modes**, the part worth getting right:

- **Structured → Raw**: call `assembleDriverYaml(form)` to produce the text raw mode starts from — so
  whatever's currently in the structured fields is what the operator sees, not a stale load.
  Straightforward, no loss (this is exactly what Save already does).
- **Raw → Structured**: call `parseDriverYaml(rawText)` on the edited text. This **can** lose information
  the structured form has no field for — anything outside `STRUCTURED_KEYS` that also isn't inside the
  boundary `rawBody`'s own split expects (a malformed or unusually-shaped file, per the doc comment
  above), or, before 178N/179N, exactly the jdbc-block risk those phases exist to close. **Warn, don't
  silently switch**: a confirm step ("Switching to the structured view may not preserve everything in
  this YAML if it doesn't match the console's own generated shape — switch anyway?") before applying
  `parseDriverYaml`'s result, only shown when re-`assembleDriverYaml`-ing the parsed result doesn't
  byte-for-byte match the raw text being left (a real diff check, not raw mode being on distrust by
  default — reopening a structured-authored driver and switching to raw and back should never nag).

**On load** (`useDriverYaml` resolving for an existing driver), default to whichever mode the file
actually round-trips cleanly in: run `parseDriverYaml` then `assembleDriverYaml` on the loaded text and
compare; a byte-for-byte match opens in Structured (today's behavior, unchanged for every existing
driver.yaml this app itself wrote), a mismatch opens in **Raw**, with a one-line note explaining why
("This file doesn't match the structured editor's expected shape — opened in raw mode so nothing is
silently reinterpreted"). This is the direct, mechanical answer to `parseDriverYaml`'s own caveat: instead
of *hoping* a hand-authored file's odd spacing splits cleanly, detect when it didn't and route around the
risk automatically.

## Plumbing

- `DriverEditPage` state: `mode: 'structured' | 'raw'`, `rawText: string` alongside the existing `form`.
- The round-trip-mismatch check (load-time default, and the confirm-before-switch check) is one small
  pure function, `roundTripsCleanly(yaml: string): boolean`, in `driverYamlAssembly.ts` — literally
  `assembleDriverYaml(parseDriverYaml(yaml)) === yaml`, exported so both call sites (load default, mode
  switch) share it rather than duplicating the comparison.
- New driver (`isNew`): starts in Structured, as today (`EMPTY`/`RAW_BODY_SKELETON`) — raw mode is for
  editing something that already deviates from the generated shape, which a brand-new driver never does
  yet.

## Out of scope here

- Validating the raw text's *content* before save — that's 181N's validate/echo panel, usable from either
  mode (raw mode gets more value from it, since it has no structured field errors to lean on, but the
  panel itself is one shared piece of UI, not duplicated per mode).
- A syntax-highlighted YAML-aware editor beyond what `CodeEditor` (already used for `rawBody` today)
  provides — no new editor component.

## How to verify when closed

- Every existing bundled/sample driver.yaml this repo ships opens in Structured mode by default
  (`roundTripsCleanly` true for each).
- A driver.yaml hand-edited outside the console to intentionally deviate from the generated shape (e.g. a
  comment placed where `splitTopLevelBlocks` wouldn't expect one) opens in Raw mode with the explanatory
  note, not silently misparsed into Structured fields.
- Editing in Raw mode and saving persists the literal text, unchanged by any structured re-derivation.
- Switching Raw → Structured on a file that would lose information shows the confirm prompt; switching on
  one that round-trips cleanly does not.
