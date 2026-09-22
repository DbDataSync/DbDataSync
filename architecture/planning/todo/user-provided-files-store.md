# `files/` — a standard place for user-provided files, and a small GUI to manage them

**Status**: Design, not phase-ready.
**Plan reference**: resolves the `jars/` vs `libraries/` question left open by
`architecture/planning/todo/jdbc-driver-support.md` and repeated in
`architecture/planning/todo/jdbc-driver-feature-gaps.md`; unblocks the jar-upload dependency named in
`architecture/planning/todo/driver-yaml-authoring-ui.md` §2 and the jar source for
`architecture/planning/todo/jdbc-ikvmreference-compile-button.md`.

## The decision

Neither `drivers/` nor `libraries/` fits a user-supplied binary like a JDBC jar — `libraries/` is
NuGet-package-shaped (`library.json`, a restored `lib/`, phase 109j's surface checking) and a jar is none
of that; `drivers/<id>/` is one descriptor's own directory, not a place for an artifact that could
reasonably be shared across more than one driver (Oracle's wallet jars, say, if more than one Oracle-via-JDBC
descriptor existed). Rather than force it into either, or mint a jar-specific `jars/` root as
`jdbc-driver-support.md` first floated: **a general, flat `files/`**, alongside the existing
`connections/`, `replications/`, `scripts/`, `drivers/`, `libraries/` (`ConfigPaths.cs`/`LibraryRegistry.LibrariesDir`
— same bare-plural-noun convention, same root). Scoped to "something an operator uploads for a driver to
reference," not to jars specifically — a JDBC jar is the motivating, and for now the only, real case, but
nothing about the mechanism is jar-shaped.

```
config/                  (managed by ConfigPaths — connections/, replications/, scripts/)
drivers/<id>/driver.yaml
libraries/<id>/library.json, lib/
files/<name>              <- new
```

## Shape on disk

```csharp
internal static class FilesPaths
{
    public static string FilesDir(string repoRoot) => Path.Combine(repoRoot, "files");
    public static string FilePath(string repoRoot, string name) => Path.Combine(FilesDir(repoRoot), name);
}
```

Flat, not per-driver-scoped — a name is unique across the whole store, the same posture `libraries/<id>/`
already takes for library ids. **The original filename is the name** (`postgresql-42.7.13.jar`), not a
GUID or a hash — this is a *manage your files* screen, and an operator recognizing what they uploaded by
its real name matters more than collision-proofing against a name nothing forces to collide. A second
upload of the same name is a **409**, the same conflict shape `DriversController.InstallFromCatalog`
already uses for a driver id that exists — replace is an explicit action (delete, then re-upload, or a
`PUT` if that's worth adding), not an implicit overwrite.

**Not gitignored.** Unlike a *restored* library's `lib/` (derived build output,
`architecture/planning/todo/jdbc-ikvmreference-compile-button.md`'s "compiled" state is the same kind of
thing), a file here is something an operator supplied on purpose — a small binary asset that belongs in
the config repo's own history the way a `driver.yaml` does, not something to regenerate. Worth confirming
this against whatever `.gitignore` pattern the config repo actually uses today before building (grep for
one rather than assume), but the intent is: `drivers/`, `libraries/*/library.json`, and `files/` are
config, `libraries/*/lib/` is derived output — `files/` belongs with the former.

## API

Mirrors `LibrariesController`'s shape closely enough to reuse the same mental model an operator already
has from that screen:

- `GET /api/files` → `[{ name, sizeBytes, uploadedAt, usedBy: [driverId, ...] }]`. `usedBy` computed the
  same way `LibrariesService.UsedBy` already scans every `driver.yaml` for a matching `library:` id — here,
  scans every JDBC-backed descriptor's `jdbc.driverJarPaths` for a matching name.
- `POST /api/files` (multipart) → one or more files. `409` per-name conflict as above; a request with N
  files and M already-conflicting names either fails the whole request or reports per-file results — worth
  deciding deliberately (leaning towards per-file results, since a multi-file Oracle-wallet upload
  shouldn't be all-or-nothing over one stale name).
- `DELETE /api/files/{name}` → `409` with the `usedBy` list if any descriptor still references it, unless
  `?force=true` — identical shape to `DELETE /api/libraries/{id}`.

`Policies.Admin`, and the same "I understand I am running this package's code in the DbDataSync host"
confirmation `drivers-and-libraries-in-the-web-ui.md` requires for a non-catalog library install — a jar
uploaded here is eventually loaded and run inside the API/TaskRunner process (via IKVM, whether translated
at runtime or compiled ahead of time), which is exactly the same trust boundary, not a lesser one.

### Size and type limits

Real numbers, not "reasonable limits" — ASP.NET's own request-body-size lever
(`RequestSizeLimitAttribute`/Kestrel's `MaxRequestBodySize`) is the existing mechanism, not new infra, but
the actual cap needs picking deliberately: the largest real JDBC driver jar in this repo's own orbit so
far (Oracle's `ojdbc8.jar` plus wallet jars) is tens of MB, not hundreds — a cap in the low hundreds of MB
per file comfortably covers every known case without leaving the door wide open. **Extension allowlist, not
denylist** — `.jar` only for v1, matching the one real use case; broadening it is a one-line change
whenever a second real use case shows up, not a reason to accept anything by default now.

## How this plugs into the two docs it unblocks

**`driver-yaml-authoring-ui.md` §2** (jar/nuget references) — its "upload control, multipart POST" was
written against an unresolved location; it's this store. The jar picker for a JDBC-based descriptor
becomes: pick from `GET /api/files` (already-uploaded jars) or upload a new one inline, exactly the same
"pick existing or add new" shape the ADO.NET side already has via `KnownLibraries` chips + search box.

**`jdbc-ikvmreference-compile-button.md`** — its source jars are files here; its *output* (the compiled
`.dll`) is not — that's derived build output, not something an operator supplied, so it stays in
`drivers/<id>/compiled/` (that doc's own open question 3) rather than landing back in `files/`. Worth
stating explicitly since both are "a binary next to a driver.yaml" and it would be easy to conflate them:
**`files/` is input a person chose; `drivers/<id>/compiled/` is output a build produced.**

## `driverJarPaths` changes shape

Phase 169V (not yet built) currently specs `JdbcDescriptorYaml.DriverJarPaths` as literal filesystem
paths — written before this store existed, provisional by its own doc comment's own admission. With
`files/` decided, the honest shape is a list of **names within that store**, not arbitrary paths:

```yaml
jdbc:
  driverClass: oracle.jdbc.OracleDriver
  driverJarPaths:
    - ojdbc8.jar          # was: /path/to/ojdbc8.jar
    - oraclepki.jar
    - osdt_cert.jar
    - osdt_core.jar
```

resolved via `FilesPaths.FilePath(repoRoot, name)` at the point `JdbcGenericDriver`'s constructor calls
`JdbcProviderFactory.FromJarPaths`, the same shape `LibraryRegistry.GetFactory` already resolves a library
id into a real path. **169V hasn't shipped yet — this changes that phase doc's own design before it's
built, not after**, so there's no migration to write, just an update to that doc before anyone implements
it from the stale version. See the update landing there alongside this doc.

## Small GUI

A new Admin tab, **Files** — plain: a table (name, size, uploaded date, used-by), an upload button, a
delete action per row (with the `usedBy`-conflict/`force` dialog `Libraries`' own delete already has a
precedent for). No preview, no in-browser jar inspection — "manage your files," not a jar explorer.
Reachable both directly (Admin → Files) and inline from the driver-authoring form's jar picker
(`driver-yaml-authoring-ui.md` §2), the same way that form already embeds the Libraries search box rather
than sending the operator away to a different screen.

## What this does not build

- Per-driver scoping / subfolders — a flat namespace, decided above, not a placeholder for a future
  hierarchy.
- Versioning (uploading `postgresql-42.7.13.jar` again as a new version rather than a name conflict) —
  out of scope; delete-and-reupload is the only path for now.
- Any change to how `libraries/` (NuGet-restored ADO.NET client assemblies) works — a parallel mechanism
  for a different kind of artifact, not a replacement for it.
