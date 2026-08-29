# Phase 52 — Identity, roles, and Windows authentication (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/authentication-and-authorization.md`

## What this phase builds

The thing every authentication method needs before any of them is worth building: a user, a role, a
session, and one method — Windows auth — proving the model works end to end. Passkeys are phase 53 and
sit on exactly this model.

There is no authentication of any kind today: `Program.cs` calls `app.UseAuthorization()` with no
authentication registered and nothing carries `[Authorize]`. Every endpoint is open to anything that
can reach the port.

## 1. The identity model

**In the state database, not the config repo.** Users are runtime state; putting them in git would
commit an access-control list to a history the UI diffs on screen. `Migrations.Scripts` gains:

```sql
CREATE TABLE Users (
    Id            TEXT PRIMARY KEY,       -- opaque, not a login name
    DisplayName   TEXT NOT NULL,
    Role          TEXT NOT NULL,          -- 'Admin' | 'Viewer'
    Enabled       INTEGER NOT NULL,
    CreatedAtUtc  TEXT NOT NULL
);

CREATE TABLE UserCredentials (
    Id            TEXT PRIMARY KEY,
    UserId        TEXT NOT NULL REFERENCES Users(Id),
    Method        TEXT NOT NULL,          -- 'Windows' | 'Passkey'
    -- Windows: the account SID. Passkey: the credential id. Unique across both, because it is what a
    -- sign-in is looked up by.
    Subject       TEXT NOT NULL,
    Secret        TEXT NULL,              -- passkey public key; null for Windows
    Label         TEXT NULL,              -- 'YubiKey 5C', 'CONTOSO\\dshryock'
    CreatedAtUtc  TEXT NOT NULL,
    LastUsedAtUtc TEXT NULL
);

CREATE UNIQUE INDEX UX_UserCredentials_Subject ON UserCredentials(Method, Subject);
```

**A credential row per method, pointing at one user.** That shape is the whole reason the requirement
"both methods, and for a single user" is cheap: signing in is "find the credential, take its user", and
adding a passkey to an account that already signs in with Windows is inserting a row. A `Users` table
carrying a `WindowsSid` column and a `PasskeyPublicKey` column would have made the same requirement a
schema change.

`Role` on the user, not on the credential: a role is what a person is allowed to do, and it does not
change with which key they used to get in.

## 2. Roles

Two, and no more: **Admin** and **Viewer**.

- **Viewer** can read everything: replications, mappings, runs, logs, metrics, verification results,
  config history, previews.
- **Admin** can additionally change anything and cause anything to happen — save config, trigger runs,
  backfill, apply provisioning, run verification checks, test connections and scripts, manage users
  and invites.

**The dividing line is "does this change something or make something happen", not "is this a GET".**
`POST /connections/{name}/test` opens a connection to a database and `POST /scripts/test` compiles and
executes operator-authored C#; both are reads in HTTP terms and neither belongs to a viewer. Every
endpoint gets an explicit attribute — the default for anything unmarked is Admin, so a new endpoint
added later is closed rather than open by omission.

## 3. Sessions

- A cookie, `HttpOnly` + `SameSite=Strict` + `Secure` when the request is HTTPS. Not a JWT: there is no
  second service to present a token to, and a cookie is revocable by deleting a row.
- Server-side session rows in the state db, so signing a user out — or disabling them — takes effect on
  the next request rather than at token expiry.
- **The SignalR hub is the surface that gets forgotten.** `/hubs/run` needs the same authorization as
  the controllers, and a test that asserts an unauthenticated connection is refused.
- **The loopback runner-state endpoint keeps its own `RunnerToken` and is not touched.** It authenticates
  a child process, not a person, and dragging it into a user scheme would mean a spawned worker needing
  a user to exist.

## 4. Windows authentication

- `Microsoft.AspNetCore.Authentication.Negotiate`, which is Kerberos/NTLM and works under Kestrel.
- Configuration names an **allowed Windows group**. On a successful negotiate, the request's
  `WindowsPrincipal.IsInRole(group)` decides whether this is a user at all.
- **Two groups, not one**, mapping to the two roles: an admin group and a viewer group. One group with
  a role assigned per user would mean a Windows shop managing DataSync roles in DataSync rather than in
  the directory they already manage groups in. A member of both is an admin.
- First sign-in creates the `Users` row and its `Windows` credential from the SID. The SID is the
  subject, not the account name — names get renamed and reused, SIDs do not.
- **Group membership is re-evaluated per request**, not baked into the session at sign-in. Removing
  someone from the group has to remove their access without waiting for a cookie to expire.

## 5. What a signed-in user changes elsewhere

- **Git attribution.** `GitAuthor` is a fixed `("DataSync API", "datasync@localhost")` singleton today,
  with a comment saying so until auth exists. It becomes per-request, from the signed-in user, so the
  config history in the Version Control tab finally answers "who". This is the change that makes the
  feature worth more than access control.
- **The SPA** gets a sign-in state, the current user in the chrome, a sign-out, and — the part that is
  not decoration — **admin-only affordances hidden for a viewer**. A Save button that always 403s is
  worse than no Save button.

## What this phase does not build

- Passkeys, invites, or user management UI beyond seeing who you are. Phase 53.
- Any authentication method beyond Windows.
- Per-object permissions. Two roles, global.
- Anything about *bootstrapping* on a non-Windows host — with only Windows auth built, a Linux
  deployment has no way in. That is phase 53's invite flow, and until it exists the escape hatch is a
  documented "authentication disabled" mode, which phase 53 removes the need for.

## How to verify when built

- Every endpoint refuses an unauthenticated request, asserted by enumerating the routes rather than by
  a hand-written list that goes stale — a new endpoint must not be able to be added without a decision.
- A viewer gets 403 on every mutating endpoint and 200 on the reading ones, including the two that look
  like reads and are not (`connections/{name}/test`, `scripts/test`).
- An unauthenticated SignalR connection is refused.
- A user removed from the Windows group loses access on their next request, not at session expiry.
- A config change made by a signed-in user is committed with that user's name, and shows in the Version
  Control tab.
- Playwright: signed-in chrome shows the user, a viewer sees no Save or Run controls, and sign-out
  returns to a sign-in screen.

## Open questions

- **How the Playwright suite authenticates.** Negotiate against a test host is not something a
  container easily does. Most likely a test-only authentication handler registered in the same way the
  API tests already override configuration — which has to be impossible to enable in a real deployment,
  and that is the part to get right rather than the part to make convenient.
- **Whether "no authentication configured" should be a supported mode or a startup failure.** A tool
  somebody just installed and ran with no arguments has no group and no passkey. Refusing to start is
  honest and unusable; starting open is usable and how products get breached. The likely answer is
  phase 53's first-run invite — the console prints a URL, and the product is closed but reachable —
  which would make this a startup failure only when *neither* method is configured.
- **Whether a Windows-authenticated user should be able to be disabled locally** while remaining in the
  group. The `Enabled` column above says yes; the group check says no. Pick one, and say which wins.
