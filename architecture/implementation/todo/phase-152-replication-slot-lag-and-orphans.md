# Phase 152 — replication slot lag, and slots nobody claims (planned)

**Status**: Planned, not started. Split out of phase 34 on 2026-09-16 — see
`architecture/implementation/done/phase-034-postgres-logical-replication.md`'s "What became its own
phase".

## Why

A logical replication slot pins write-ahead log on the source from the moment it is created until
something advances it. Phase 34 made two deliberate choices that make this visible rather than
theoretical: advancing is **off by default** (a shared slot advanced by whichever mapping ran last
discards changes the others have not read, so advancing unasked is unsafe), and nothing drops a slot
when its mapping is deleted (phase 151).

Both are the right defaults and both mean **the normal state of a working deployment is a slot that is
holding WAL**. An operator needs to see how much, before the disk fills rather than after. Right now
there is nowhere that number appears.

## What this builds

**Slot lag on the connection card** — phase 19's card is the natural home, as the phase 34 plan said.
`pg_current_wal_lsn() - confirmed_flush_lsn` is the retained bytes, and
`PgLogicalSlotStatement.SlotState` already returns it; nothing reads it yet.

**Slots nobody claims** — `PgLogicalSlotStatement.OurSlots` already lists every logical slot named
`dbdatasync_%` with what it is pinning. Cross-referencing that against the slots the configuration
actually names is what turns it into a report: a slot in the list and not in the config is one this tool
created and forgot, and it is costing the source disk for nothing.

The prefix exists precisely so this is answerable — a slot somebody else's replication put there is
never named `dbdatasync_`, so this can report without ever proposing to drop something that is not ours.

## What is already done, so this is smaller than it looks

The statements are written and covered by phase 34's tests. What is missing is an API surface and a
place on the screen, which is where the work actually is: a Postgres-specific reading on a card that is
driver-neutral today, so the shape question — does the card ask the driver for "things worth reporting
about this connection", or does it know about slots — is the real design decision here.

The driver-neutral framing is worth taking seriously rather than special-casing Postgres: SQL Server CDC
has an equivalent number (a capture instance whose cleanup job has fallen behind retains log too), and
so does a trigger-audit shadow table that nothing prunes. One "what is this source holding on this
replication's behalf" reading would cover all three.

## How to verify when built

- A slot with unread WAL reports a non-zero retained size on its connection's card, and the number moves
  when the slot is advanced.
- A slot named `dbdatasync_%` that no mapping's configuration names is reported as unclaimed.
- A slot belonging to something else entirely is never reported.
- `Category=Integration`, against the same `wal_level=logical` container phase 34 added — the numbers
  come from the server, so a test with a stubbed one would prove nothing.
