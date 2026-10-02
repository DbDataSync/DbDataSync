# Phase 199H — HTTP.sys on a real host: verification and CI coverage

**Status**: Not started. Carries the verification phase 196H could not do.

## Why

CI on Windows only builds the host under HTTP.sys and checks the auth bridge's configuration. Nothing yet
starts it, so the claims in `docs/configuration.md` about sharing a port, Windows sign-in, SignalR and
uploads are untested. The first CI run on 196H found a wrong scheme name that no Linux test could have.

## Checklist

1. Two listeners on one port, different host names: both answer. (The second can be a trivial
   `HttpListener`/`UseHttpSys` process started by the test.)
2. `POST /api/auth/windows` unauthenticated gets a `401` with `WWW-Authenticate: Negotiate`; with default
   credentials, a member of the configured group gets a session and a non-member gets the 403 text.
   Kerberos and the NTLM fallback.
3. `/hubs/run` connects over WebSockets and receives progress.
4. A file upload over HTTP.sys's default body limit succeeds (the endpoint sets 200 MB per request).
5. `RemoteIpAddress` is what loopback trust (`Auth:Network:*`) and `RunnerStateGuard` expect.
6. Run as a service under `LocalSystem`.
7. https via `netsh http add sslcert`, with and without the Kestrel certificate keys also set.

## Approach

- A `Category=Windows` test in `DbDataSync.Api.Tests` that starts the host on a high port under HTTP.sys and
  drives items 2–4 over HTTP. GitHub's Windows runner is an administrator, so no URL reservation is needed
  there; item 1 and the non-admin cases belong to phase 198H.
- Items 5–7 need a real machine; do them once by hand and record the result in this doc's retrospective.
- Decide, from the results, whether the default can move (phase 196H, "Default").
