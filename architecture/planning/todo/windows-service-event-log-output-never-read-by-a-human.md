# Phase 135/136's Windows Event Log and `icacls` work: pass/fail confirmed on CI, literal output never read

**Status: a small, well-bounded verification gap — not started.** Extracted from
`architecture/implementation/done/phase-136-windows-service-startup-diagnostics.md`'s own "What's
honestly still unverified" section and
`architecture/implementation/done/phase-140-windows-ci-verification-and-remaining-failure.md`'s own "CI
result" section, both of which named this and left it — moved here per
`architecture/implementation/README.md`'s "Follow-up work gets its own doc, not a paragraph."

## What's actually still open, after phase 140

Phase 136 shipped Windows service startup diagnostics (Event Log writes) and named three things as
"honestly still unverified," needing a real Windows box. Phase 140 later got a real `dotnet-windows` CI
run and closed most of this **by inference, not by direct observation**: a green `Test` step on
`windows-latest` means every non-Integration test passed, including
`WindowsServiceEventLogTests`' three real Event Log round-trip tests and phase 135's own real `icacls`
ownership-transfer test — so the tests *ran and passed*, confirmed by the step's own exit code rather
than by arithmetic (phase 140's earlier, weaker evidence). Phase 140's own words: "The green Test step is
a stronger signal than the arithmetic it replaces, but it is a pass/fail signal, not the output itself.
Naming the remaining gap rather than calling it closed."

**What remains open, precisely:**

1. **Nobody has read the literal Event Log / `icacls` text the tests produced.** The tests assert
   specific content (an event exists under source `DbDataSync`, with certain fields), and they passed —
   but the actual log lines have never been read by a human, only inferred from a green checkmark. Phase
   140 tried and couldn't: reading the raw job log needs an authenticated `gh` (the unauthenticated logs
   endpoint 403s), and no session so far has had one.
2. **Phase 136's own Checkpoint 6 — a *manual* scenario, not a test** — reproducing phase 135's original
   Error 1053 startup failure against a real installed Windows service, and confirming the diagnostic
   message appears in Event Viewer without the Scheduled-Task workaround phase 135's own investigation
   needed. This was never a test at all, so no CI run — green or not — closes it. Still not done.
3. **Whether a *non-elevated* real Windows install's first `service install` can call
   `EventLog.CreateEventSource` successfully.** `windows-latest` GitHub runners are elevated by default
   (noted in `WindowsServiceEventLogTests`'s own doc comment), so CI passing says nothing about this
   case. An operator's own non-elevated shell hitting this for the first time is still unverified.

## Why these are worth closing, not just noting again

Items 2 and 3 are exactly the kind of gap a real operator could hit that CI structurally cannot catch —
CI runs elevated and the reproduction in item 2 needs a hand-installed, hand-broken service. Item 1 is
lower-stakes (a green test is a real signal) but cheap to close once someone has authenticated `gh`
access, and would upgrade "the tests presumably assert something reasonable" to "confirmed, by reading
it."
