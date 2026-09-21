# `dbdatasync setup`'s action bar doesn't respond to keyboard or Ctrl+C on real Windows, and Exit doesn't work even by mouse

**Status: resolved 2026-09-21 — root cause confirmed and fixed, not just diagnosed.** Reported from manual
testing on a real Windows box. Turned out not to be Windows-specific, and not a Terminal.Gui defect at
all — see below.

## What was observed

Running `dbdatasync setup` interactively on Windows:

1. Keyboard focus (Tab/Shift-Tab, arrow keys) never reached the action bar (Save/Start/Print
   config/Reissue invite/Exit) — it navigated fine everywhere else (tabs and their fields).
2. Clicking Exit with the mouse visually responded but did nothing.
3. Ctrl+C did nothing either.

## Root cause: `actionBar` was never made focusable

`SetupScreen.cs`'s `actionBar` is a plain container `View`:

```csharp
var actionBar = new View
{
    X = 0, Y = Pos.AnchorEnd(4), Width = Dim.Fill(), Height = 4,
};
actionBar.Add(saveButton, startButton, printButton, reissueButton, exitButton);
```

Terminal.Gui containers default to `CanFocus = false`, and setting `HasFocus` on any view recursively
requires every SuperView up the chain to also be focusable — so a non-focusable container makes every
button inside it unreachable, by keyboard *or* mouse, on any driver. This is the exact trap every tab
already had its own copy of and its own fix for: `GeneralTab.cs`'s own comment says it outright —
"container Views default to non-focusable; without this the focus chain never reaches these fields" —
and every one of the six tabs sets `CanFocus = true` on itself. `actionBar` was simply the one container
that got missed, since it isn't a tab.

**Nothing about this was Windows-specific.** It would have failed identically on Linux or macOS; it was
just never exercised interactively anywhere before, on any platform — `SetupScreenCaptureTests` (the only
test that runs the real `SetupScreen`) only ever asserted on a single rendered frame, never a keypress or
click. Ctrl+C doing nothing is unrelated and expected: no custom quit-key handling exists anywhere in
`Tui/`, so that's just Terminal.Gui's own default (not bound to Ctrl+C in this version).

## The fix

One line, in `SetupScreen.cs`:

```csharp
var actionBar = new View
{
    X = 0, Y = Pos.AnchorEnd(4), Width = Dim.Fill(), Height = 4,
    CanFocus = true,
};
```

## Verified, not just asserted

A regression test (`SetupScreenCaptureTests.ExitButton_CanBeFocusedAndActivatedFromTheKeyboard`) drives
the real `SetupScreen` headlessly via `Terminal.Gui.Testing.IInputInjector`: it calls `exitButton.SetFocus()`
directly (the same pattern `TabWiringTests` already uses to bypass fragile Tab-order traversal) and asserts
it succeeds, then injects a real `Enter` keystroke and asserts the screen actually stops with exit code 0.

Confirmed both directions, not just the passing case:
- **With the fix**: passes in ~1 second — `exitButton.SetFocus()` returns `true`, Enter triggers
  `Accepted`, `RunAsync` returns `0`.
- **With the fix reverted** (`actionBar.CanFocus` back to its implicit `false`): fails immediately with
  "Exit could not be focused — did actionBar lose its CanFocus = true?" — not a hang, because the test
  carries its own bounded safety-net (`RequestStop()` by iteration 5) so a broken build fails fast in CI
  instead of hanging a test run forever.

One thing worth naming for whoever picks this up next: the *first* version of this test hung for 17+
minutes instead of failing, because an `Assert.True(...)` thrown from inside `app.Iteration`'s handler
was silently swallowed by Terminal.Gui's main loop rather than propagating — nothing ever called
`RequestStop()` again, so the run loop spun forever. The working version moves every assertion outside the
`Iteration` handler (capturing state into local variables instead) and adds the bounded safety-net stop.
Any future Terminal.Gui headless test in this codebase should do the same — never assert from inside an
`Iteration` callback.

Full CLI test suite (`DbDataSync.Cli.Tests`) reran clean after the fix: 227 passed, 11 skipped
(Windows-only tests, expected on this Linux dev box), 0 failed.
