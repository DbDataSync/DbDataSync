# Phase 196H — Opt-in HTTP.sys for port sharing

**Status**: Implemented; the HTTP.sys runtime path has **not yet been exercised on Windows**. Move to `done/`
once the verification list below has been run on a real host.

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

## Verification still owed (Windows)

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

1. **Certificates.** Everything certificate-shaped still speaks Kestrel: `cert bind`, `use-pem`, `use-pfx`,
   `new-self-signed`, the readiness check, the Admin certificate screen, the TUI tab and
   `CertificateExpiryService` all read or write `Kestrel:Certificates:Default:*`. Under HTTP.sys the
   certificate is bound to the port in the OS. Needed: a binding abstraction over both models (PEM/PFX files
   would have to be imported to the store before binding), expiry and status reading the HTTP.sys binding,
   and a restart-free rebind, which HTTP.sys allows. This is the largest gap and the main reason the default
   should not flip yet — an existing install's certificate would silently stop being used.
2. **URL reservations.** `service install` does not run `netsh http add urlacl` (and `uninstall` does not
   remove it), so a non-admin service account fails to bind until an operator does it by hand.
3. **Path prefixes.** Sharing by path (`/dbdatasync/`) does not work: HTTP.sys sets `PathBase` from the prefix,
   but the SPA, its assets and `/api` calls assume the root of the origin. Needs a base path in the Vite build
   or served `<base>`, and the fallback/hub routes to honour it.
4. **URL grammar.** `App:Url` means a different thing: `0.0.0.0` is invalid and `localhost` only matches that
   `Host`. Anything else that consumes `App:Url` (`health`, `invite`, `serve`'s own messages) has not been checked
   against the HTTP.sys meaning. Worth normalising or at least validating up front with a clear message.
5. **Request limits and timeouts.** No settings surface for HTTP.sys's own `MaxRequestBodySize`, timeouts or
   request queue; Kestrel's are not configurable here either, so this is parity work, not a regression.
6. **Test coverage.** `TestServer` has neither Kestrel's nor HTTP.sys's connection features, so the
   authentication path only has the Windows-only build test; real coverage needs a Windows CI job that starts
   the host.
7. **Startup-failure reporting** (phase 136) does not yet recognise `HttpSysException` (access denied,
   prefix already registered) to give the actionable message it gives for a port conflict.
8. **Default.** Decide Kestrel vs HTTP.sys as the default for a Windows service once 1–4 are closed.
