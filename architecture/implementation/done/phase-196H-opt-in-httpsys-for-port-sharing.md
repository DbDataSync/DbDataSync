# Phase 196H — Opt-in HTTP.sys for port sharing

**Status**: Built. CI on Windows exercises only the host build and the auth bridge's forward target; the
HTTP.sys runtime path (listening, sharing a port, sign-in, SignalR, uploads) has **not been run on a real
host**. That verification is carried by phase 199H rather than holding this one open.

## Why

Kestrel owns its port exclusively. When the console has to share a port with another service on the same
machine (both on 80/443, told apart by host name), the only listener that allows it is HTTP.sys. The goal
here is the minimum that gets port sharing and Windows sign-in working behind an opt-in switch, with the
remaining gaps written down rather than papered over — so the default can be decided later with the gaps in
view.

## What was built

- `DbDataSync:App:Server` = `kestrel` (default) | `httpsys`, read by `WebServerSelection`. An unknown value
  refuses to start instead of falling back, because a silent fallback to Kestrel presents as "port in use"
  against the very process the operator meant to share with. `httpsys` off Windows refuses to start too.
  Surfaced in `ApiOptions.Server` and the Admin config screen; documented in `docs/configuration.md`
  ("Sharing a port with HTTP.sys").
- `HttpSysHosting` switches the main host over, **by reflection**: the reference assembly for
  `Microsoft.AspNetCore.Server.HttpSys` is a resource-strings-only stub on some Linux SDKs (found on this
  repo's dev box), so a direct `UseHttpSys` call would break the build there. Collapses to a direct call if
  that stops mattering. Anonymous stays allowed; Negotiate/NTLM are offered only when Windows sign-in is
  configured.
- **Authentication.** `AddNegotiate()` is not registered under HTTP.sys. Instead a policy scheme named
  `Negotiate` forwards to HTTP.sys's own handler, so `AuthController`'s
  `[Authorize(AuthenticationSchemes = "Negotiate")]`, `WindowsSignIn` and the session issuing are untouched.
- A startup warning when `Kestrel:Certificates:*` is configured but ignored, and when the bound URL has a path
  prefix.
- Unchanged on purpose: `StateHost` (a separate loopback Kestrel server for the task runners — it never
  needed to share a port), SignalR, static files, the SPA fallback, `/api/health`.

## Found by CI on Windows

The first push forwarded the `Negotiate` bridge to a scheme name written from memory
(`Microsoft.AspNetCore.Server.HttpSys`); HTTP.sys's real scheme is `Windows` (`HttpSysDefaults`). It would have
failed at the first Windows sign-in. HTTP.sys also registers that scheme in its server's constructor, i.e. at
startup, so a built-but-unstarted host does not have it — the test now compares the bridge's forward target
with the real constant instead of looking the scheme up. Read from the decompiled 10.0.12 assembly.

## Verification not yet done (now phase 199H)

1. `serve` with `Server: httpsys`, plain http on a host-name prefix, with another HTTP.sys listener on the same
   port and a different host name: both answer.
2. Windows sign-in end to end (`POST /api/auth/windows`): a member of the configured group gets a session; a
   non-member gets the 403 text. Kerberos and NTLM fallback.
3. SignalR (`/hubs/run`) connects over WebSockets and receives progress.
4. A file upload over the default body limit (the endpoint sets 200 MB per request) succeeds.
5. `RemoteIpAddress` is what the loopback-trust (`Auth:Network:*`) and `RunnerStateGuard` checks expect.
6. Run as a service under `LocalSystem`, and under a named account with and without a URL reservation — the
   latter should fail with an access-denied startup message that is legible in the Event Log (phase 136).
7. https via `netsh http add sslcert`, and the startup warning when the Kestrel certificate keys are also set.

## Deliberately left for later (the gap to Kestrel)

Each item has its own phase doc in `todo/`:

1. **Certificates** — phase 197H. The largest gap and the main reason the default should not flip yet: an
   existing install's certificate would silently stop being used.
2. **URL reservations, `App:Url` grammar, startup-failure messages** — phase 198H.
3. **Real-host verification and CI coverage** — phase 199H.
4. **Path prefixes** — phase 200H.
5. **Request limits and timeouts.** No settings surface for HTTP.sys's own `MaxRequestBodySize`, timeouts or
   request queue. Kestrel's are not configurable here either, so this is parity work, not a regression; take it
   up if a deployment needs it.
6. **Default.** Decide Kestrel vs HTTP.sys as the default for a Windows service once 197H and 198H are closed
   and 199H has run clean.

## Retrospective

- The auth bridge's first version forwarded to a scheme name written from memory; CI on Windows caught it
  (see "Found by CI on Windows"). Anything that names an HTTP.sys type, scheme or option should be checked
  against the shipped assembly, not recalled — a decompiler on the Windows runtime package is a quick way,
  since the Linux reference assembly for HTTP.sys is a stub.
