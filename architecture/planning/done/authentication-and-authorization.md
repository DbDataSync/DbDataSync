# Authentication — who is using this, and what may they do

**Status**: Resolved. The design is split across
`architecture/implementation/todo/phase-052-identity-roles-and-windows-auth.md` and
`architecture/implementation/todo/phase-053-passkeys-and-invites.md`.

## The ask

- Two authentication methods to start with: **Windows auth** and **passkeys**.
- Windows auth authorises by **Windows group** — a named group is the allowed users.
- Passkeys need an **invite code/URL** system. The first one is printed to the console on first
  startup; after that an existing user generates one in the UI to send to somebody new.
- **Both methods usable at once**, and **by a single user** — one person can sign in with either.
- Two roles for now: **admin** and **viewer**.

## What was found when we looked

- **There is no authentication at all today.** `Program.cs` calls `app.UseAuthorization()` with no
  authentication middleware registered and no `[Authorize]` on any controller. Every endpoint is open
  to anything that can reach the port.
- **Every config commit is attributed to a fixed identity** — `new GitAuthor("DataSync API",
  "datasync@localhost")`, with a comment saying it stands in until per-user auth exists. The config
  repo is a git history nobody can read the "who" of. Auth is what makes that history true, and it is
  the reason this is worth doing beyond access control.
- **`architecture/detailed-design.md` §8 lists this as an open question**, with "v1 may need to assume
  a trusted-network single-user deployment". This closes it.
- **The state store already has a migrations mechanism** (`Migrations.Scripts`), which is where users,
  credentials and invites belong — they are runtime state, not git-tracked config. Putting users in
  the config repo would commit an access-control list to a git history and diff it in the UI.
- **The loopback runner-state endpoint has its own token** (`RunnerToken`). It is not a user and must
  not be dragged into a user-facing scheme; whatever gets built has to leave that path alone.
- **SignalR** (`/hubs/run`) is a second surface that needs the same answer as the controllers, and is
  the one that is easy to forget.

## Why it is two phases

The two methods share one identity model, and the "one user, both methods" requirement is the reason:
a user is a row, and a Windows account and a passkey are both *credentials* pointing at it. That model
has to exist before either method is worth building.

So: the first phase builds the identity model, the roles, the session, and Windows auth — the simpler
of the two methods, and the one that needs no new UI beyond a sign-in state. The second builds
passkeys and the invite flow on top of it. Each is independently verifiable, and the split is at the
seam the requirement itself draws.

## Deliberately not in scope

Anything resembling a directory integration beyond a group check, per-object permissions, or a third
authentication method. Two roles and two methods is what was asked for, and every one of those is a
door that is hard to close once opened.
