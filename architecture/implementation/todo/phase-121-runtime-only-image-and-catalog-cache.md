# Phase 121 — a runtime-only image with a pre-built catalog cache (planned)

**Status**: Planned, not started — **follow-on, deferred until phases 116–120 are in production.**
**Plan reference**: `architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`
§*Follow-on work* → *A runtime-only image + a pre-built catalog cache*. Depends on phase 117 (the
catalogs), phase 120 (the SDK-based default image and the install endpoints).

## Why

Phase 120 makes the default Docker image SDK-based so ad-hoc library install works out of the box.
That image is large. Some deployments want a small, hardened footprint and only ever install
pre-vetted (catalog) drivers — for them, a runtime-only image with the catalog libraries baked in
covers the whole realistic need without an SDK in the running container.

## What this builds

- A second published image — `dbdatasync:<v>-runtime` (or the naming decided in review) — on
  `mcr.microsoft.com/dotnet/aspnet:10.0`.
- A hidden `dbdatasync internal build-catalog-cache <out-dir>` command that runs the same
  `LibraryInstaller` code the runtime would, once per `KnownLibraries` entry, at its catalog-pinned
  version. Invoked in the Dockerfile's existing `sdk:10.0` build stage; the output
  (`<id>/lib/` + `library.json` per entry) is `COPY`'d into the runtime image at
  `/opt/dbdatasync/library-cache/`. The RID matches the runtime base by construction.
- On this image, `LibraryRegistry` / the install endpoints detect "no SDK available" and:
  - a **catalog** install (`POST /api/drivers/from-catalog`, or `POST /api/libraries` for a
    `KnownLibraries` id at its pinned version) **copies from the in-image cache** into
    `<repo>/libraries/<id>/` — zero SDK, zero network, works air-gapped.
  - a **non-catalog** install writes and commits `library.json` immediately, marks the library
    **pending restore** — a distinct state from "does not resolve" on the Libraries screen — and
    surfaces `dbdatasync config library sync`, runnable wherever an SDK exists, including
    `docker run --rm -v <vol>:/data dbdatasync:<v> config library sync` against the SDK image.
- `config library sync` extends to complete a pending restore (write `lib/` from a `library.json`
  that has none yet), not only to re-restore an existing one.
- The Libraries screen renders the "pending restore" state with the exact command to finish it.

## How to verify when built

- CI builds both images; the runtime one has no `dotnet` SDK (`dotnet --list-sdks` empty) and starts
  and serves.
- An integration test on the runtime image: `POST /api/drivers/from-catalog` for `mysql.generic`
  succeeds with no network (cache copy), the driver loads after a restart, a MySQL → SQL Server
  round-trip passes (reusing phase 116's end-to-end fixture).
- On the runtime image: `POST /api/libraries` for a non-catalog package → the library is written,
  committed, and shown "pending restore"; `config library sync` on the SDK image against the same
  volume completes it.
- The catalog cache in the image matches every `KnownLibraries` entry at its pinned version (a
  build-time assertion).

## What this does not build

- Dropping the SDK from the *default* image — phase 120's decision stands; this is an alternative,
  not a replacement.
- A cache for compiled-driver packages or arbitrary non-catalog libraries — the cache is exactly the
  curated set.
- Multi-version caching — one pinned version per catalog entry.

## Open questions

- Image naming: `-runtime` suffix on a default that has no suffix, or make the runtime one the
  unsuffixed default and the SDK one `-sdk`. Phase 120 committed to "SDK is the default"; this is
  just the tag string.
- Whether the catalog cache is worth shipping in the *SDK* image too (so even it does catalog
  installs offline). Leaning: yes, it's cheap and removes the network dependency for the common
  path everywhere.
- Size of the full catalog closure (Oracle and its native-asset packages dominate). Measure; if it's
  large, consider caching only entries that have a `KnownDrivers` binding (just `mysql-connector`
  for v1).
