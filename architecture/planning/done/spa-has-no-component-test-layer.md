# The SPA has no component-test layer, so every UI assertion is an E2E one

**Status: resolved 2026-09-03 — the E2E-in-CI half is worth building now
(`architecture/planning/done/playwright-suite-in-ci.md`); a component-test toolchain stays deferred, not
urgent enough to justify a second toolchain to maintain indefinitely.**

Phase 96's plan called for "unit / component" tests of one control's validation — that adding a target
column appends a row, that a duplicate is refused, that the control renders when the catalog offers
nothing. There is nowhere to put them. `src/DbDataSync.Web/package.json` has no vitest, no
`@testing-library/react`, no `test` script; every SPA test in this repo is Playwright, and CI runs only
`npm run build`.

They were written as Playwright tests instead, against a fully network-stubbed page — which is the
established pattern here (`lag-monitoring.spec.ts` and `runs-watermarks-refresh.spec.ts` both stub every
request) and works well. So this is not urgent.

## What it costs

- Three assertions about a pure function of props needed a browser, a dev server, an API process and a
  route table to make.
- A rule that exists in two languages — `ColumnAutoMap` (C#) and `autoMap` (TS), see phase 95 — can only
  be pinned on the C# side. The TS side has no unit to test.
- The cheapest possible test of a validation branch currently costs a page load, so branches tend not to
  get tested.

## What it would take

vitest, `@testing-library/react`, jsdom, a `test` script and a CI step. Small in itself; the real
decision is that it is a second test toolchain to keep working, and that the line between "component
test" and "stubbed E2E" then has to be drawn by whoever writes the next test.

## Worth deciding alongside

**The Playwright suite is not in CI either.** `.github/workflows/ci.yml` builds the SPA and runs the
.NET tests; nothing runs the E2E suite. A defect in a flow that suite covers can therefore ship — and
one did: see `apply-button-does-not-cache-provisioned-columns.md`, which test 18 has been catching
locally since phase 94 with nobody looking. Adding a component layer while the E2E suite still runs
nowhere but a developer's machine would be solving the smaller half.

**Next step**: E2E in CI is being built (`playwright-suite-in-ci.md` → phase 98). The component-test
layer stays a recorded thought, not scheduled.
