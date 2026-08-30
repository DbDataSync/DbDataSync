# Phase 53 — Passkeys, and the invite that bootstraps them

**Status**: Done
**Plan reference**: `architecture/planning/done/authentication-and-authorization.md`
**Depends on**: phase 52, which builds the identity model this sits on.

## What this phase builds

The second authentication method, and the thing that makes it usable on a host with no Windows domain:
an invite. A passkey is a credential row against the same `Users` table phase 52 created, so a person
can hold a Windows credential and a passkey at once and sign in with either.

## 1. Passkeys

- `Fido2` / `Fido2.AspNet` (fido2-net-lib). WebAuthn is not something to implement from the
  specification for a two-role admin tool.
- Four endpoints, the standard shape: begin/complete registration, begin/complete assertion. The
  challenge is held server-side against the session, not round-tripped to the client.
- The stored credential is a **public** key. That is why it lives in `UserCredentials.Secret` in the
  state database rather than in the `SecretStore`: there is no secret to protect. Saying so in the code
  matters, because the column's name invites the opposite assumption.
- **The relying party id is the origin, and it is the thing that will go wrong.** A passkey registered
  against `localhost` does not work against `datasync.corp.example`, and one registered against an IP
  does not work at all. It has to be configuration, it has to be validated at startup, and a mismatch
  has to say what it is rather than failing inside a browser API. Expect this to be the whole support
  burden of the feature.
- Multiple passkeys per user, listed with their labels and last-used times, individually removable.
  Losing your only key on a tool with no password reset is the failure mode; the answer is being able
  to enrol a second one and being told to.

## 2. Invites

An invite is a one-time capability to create a user, or to add a credential to one.

```sql
CREATE TABLE Invites (
    Id           TEXT PRIMARY KEY,
    CodeHash     TEXT NOT NULL,        -- the code itself is never stored
    Role         TEXT NOT NULL,        -- what the invited user becomes
    UserId       TEXT NULL,            -- set when adding a credential to an existing user
    CreatedBy    TEXT NULL,            -- null for the bootstrap invite: nobody made it
    CreatedAtUtc TEXT NOT NULL,
    ExpiresAtUtc TEXT NOT NULL,
    RedeemedAtUtc TEXT NULL,
    RedeemedByUserId TEXT NULL
);
```

- **The code is hashed, never stored.** It is a bearer credential that arrives over chat or email and
  is worth exactly what a password is worth. A database somebody can read is a database somebody can
  sign in from.
- Single use, and short-lived — hours, not weeks — with the expiry shown when it is generated so the
  sender knows what they are sending.
- Redeeming takes the invite to a page that runs a passkey registration and, on success, creates the
  user with the invite's role.
- An invite for an **existing** user (`UserId` set) adds a passkey instead of creating anybody: this is
  how a Windows-authenticated user enrols a key for when they are off the domain, which is the "one
  user, both methods" case made concrete.

## 3. First run

- On startup, if there are **no users at all**, the server mints a bootstrap invite for an Admin and
  **prints the URL to the console**. That is the only way in on a fresh install, and it is the reason a
  fresh install can be closed by default rather than open.
- It is reprinted on every start while there are still no users, because the first line of a service's
  log is not somewhere anybody looks twice, and it is revoked the moment the first user exists.
- `datasync invite` as a CLI subcommand (phase 51's tool) for the case where the console has already
  scrolled or the process is a service with its output going nowhere. A service with no console is the
  normal case on Windows, so this is not a convenience.

## 4. Managing users

A Users screen, admin-only:

- Who exists, their role, their credentials, when each was last used.
- Generate an invite — for a new user with a chosen role, or for an existing user to add a credential.
  The result is a URL to copy, shown once.
- Change a role, disable a user, remove a credential.
- **An admin cannot remove their own last credential or demote themselves to the last viewer.** Locking
  everybody out of the tool that manages the lockout is a real way to lose an afternoon, and the check
  is three lines.

## What this phase does not build

- Password authentication. It is not asked for, and adding it would mean password reset, rotation and
  storage — three problems passkeys exist to avoid.
- Email delivery of invites. The URL is copied by a person who already has a way to reach the invitee.
  An SMTP configuration is a whole feature.
- Recovery codes. Enrolling a second passkey is the recovery story; a code somebody writes down is a
  password with extra steps.

## How to verify when built

- Registration and assertion against a real WebAuthn implementation — a virtual authenticator, which
  Chrome DevTools Protocol exposes and Playwright can drive, so this is testable end to end rather than
  by mocking the library.
- An invite redeems once; the second attempt fails and says why.
- An expired invite fails and says so.
- A bootstrap invite is printed on a fresh install, works, and is gone once a user exists.
- A user with both a Windows credential and a passkey signs in either way and is the same user — same
  id, same role, same git attribution.
- An admin cannot remove their own last credential.
- A relying-party-id mismatch is reported at startup, not in the browser.

## Open questions

- **What happens to a passkey when the relying party id legitimately changes** — a deployment that moves
  from `localhost` to a hostname. Every existing passkey stops working, and the honest answers are "say
  so loudly at startup" and "make it easy to re-enrol", not "migrate them", which is not possible.
- **Whether an invite URL should carry the code in the path or the fragment.** A fragment is not sent to
  the server and not written to access logs, which matters for a bearer token; it also means the
  redemption page has to read it client-side and post it. Probably worth it.
- **Whether the bootstrap invite should be suppressible** for a deployment that intends to use only
  Windows auth and never wants a passkey path. Likely a configuration flag, but "no users yet and no
  way in" needs an answer before that flag can exist.

---

# Retrospective

A fresh install is **closed by default** and still usable: the first start prints an invitation URL,
opening it enrols a passkey, and that account is the first administrator. Verified by running the tool
against an empty directory — every endpoint answered 401, `/api/auth/status` answered honestly, and the
invite was on the console.

## The identity model paid off exactly as designed

Adding a passkey to somebody who already signs in with Windows is one row in `UserCredentials`. Phase
52's shape was chosen for this and it needed nothing.

## The code goes in the fragment

`/invite#<code>`. Browsers do not send a fragment to a server, so a bearer credential that arrives over
chat does not end up in every proxy and access log between the sender and the recipient. The redemption
page reads it client-side and posts it deliberately.

Stored as a SHA-256 hash and never as itself — no salt, deliberately: it is a 256-bit random value, not
a password, so there is no dictionary to defend against and a per-row salt would buy nothing.

## Single use is a WHERE clause, not an intention

`UPDATE Invites SET RedeemedAtUtc = … WHERE Id = … AND RedeemedAtUtc IS NULL`. Two redemptions racing
each other both find the invite valid; only one of them updates a row, and the other gets a 409. The
test asserts the second attempt fails rather than asserting the flag was set.

Checking a code says only yes or no. "Expired", "already used" and "never existed" are one answer to
somebody working through guesses, and the test asserts the two responses are byte-identical.

## The relying-party id is validated at startup, because it is the whole support burden

A passkey registered against `localhost` does not work against `datasync.corp.example`, and one
registered against an IP address does not work at all — WebAuthn requires a domain. All three are
checked when the app starts and reported as a warning naming the id and the origins, because the
alternative is a browser API refusing a ceremony with a message that names neither.

A warning rather than a refusal: Windows authentication may be the only method a deployment intends to
use, and refusing to start over a feature nobody configured would be worse than the problem.

## `datasync invite` broke a safety rule, and the exception is argued rather than assumed

`StateOwnershipTests` asserts that only the API process opens the state file — phase 39's rule, which
exists because *runner processes* are spawned constantly and concurrently and many writers against one
SQLite file was the bug it fixed. The invite command opens it directly, because the situation it exists
for is "nobody can sign in", and an endpoint needing a session is no help there.

The test now lists the file by name with the reasoning, rather than relaxing the rule to a pattern.
The next file that wants an exception has to argue for it in the same place.

## Locking everybody out is three lines of check

The only enabled administrator cannot demote or disable themselves, and nobody's last credential can be
removed. Both are refused with a sentence saying what to do first. An account with no credential can
only be recovered with an invitation, which is a support call rather than a decision anyone meant to
make.

## Verification

- `InviteTests` (8) — minting as an admin and the URL's fragment, a viewer refused, the code absent
  from the database, lookup by code only, single use, expiry, a check that says nothing about why, and
  redemption reachable without a session.
- `BootstrapInviteTests` (5) — minted when there are no users, replaced rather than accumulated across
  restarts, revoked once somebody exists, an admin's own invite surviving that revocation, and nothing
  minted when authentication is off.
- `UserManagementTests` (7) — the list, a viewer refused, the only admin unable to demote or disable
  themselves, both allowed once there is a second, and a last credential refused until there is another.
- `PasskeyOptionsTests` (6) — a matching origin, a subdomain, a URL as the id, an IP as the id, an
  origin outside the relying party, and the defaults being self-consistent.
- Manual, end to end: the tool run against an empty directory printed the bootstrap invite, refused
  every endpoint, and `datasync invite` produced a fresh one for both roles.

## What is not covered, and why

**No test completes a WebAuthn ceremony.** The plan hoped for Chrome's virtual authenticator through
Playwright, and that would work — but the Playwright suite runs with authentication *disabled* (phase
52's decision, for the Kerberos reason), so a passkey flow there would need a second suite configured
differently. The library's own verification is not re-tested here; what is tested is everything around
it, which is where the security properties live.

That leaves the registration and assertion round trip proven only by the library's own tests and by
running it. Said plainly rather than covered by a mock, which would test the mock.

## Open questions

- ~~**Path or fragment.**~~ Fragment, for the log reason above.
- ~~**Suppressing the bootstrap invite.**~~ Not a flag: it is already suppressed by
  `DataSync:Auth:Disabled`, and a Windows-only deployment that leaves it on simply has one unused
  invitation that expires in a day and is revoked the moment anybody signs in.
- ~~**A relying-party id that legitimately changes.**~~ Every enrolled passkey stops working, and there
  is no migration possible — the startup warning names it and enrolling again is the answer.
- **New**: a second Playwright project, configured with authentication on and a virtual authenticator,
  is the only way to cover the ceremony end to end. Worth it before this is relied on in anger.
