# Phase 198H — HTTP.sys: service install, URL grammar and startup failures

**Status**: Not started. Follow-up to phase 196H.

## Why

Three rough edges that make HTTP.sys fail in ways an operator cannot easily read, none of them hard.

## Scope

1. **URL reservations.** HTTP.sys needs a URL ACL for the service account unless it is `LocalSystem` or an
   administrator. `service install` should run `netsh http add urlacl url=<prefix> user=<account>` when
   `App:Server` is `httpsys` (reading it the way the command already reads `App:Url`), `service uninstall`
   should remove it, and a re-install with a changed `--url` or `--account` should not leave a stale one
   behind. Needs elevation, which the command already requires.
2. **`App:Url` grammar.** Kestrel accepts `http://0.0.0.0:8080` and `localhost`; HTTP.sys does not accept
   `0.0.0.0`, and `localhost` only matches that `Host` header. Validate `App:Url` up front under HTTP.sys
   with a message naming the equivalent (`+`, `*`, or a host name). Check every consumer of `App:Url`
   (`health`, `invite`, `serve`'s own messages, the container entry point, the passkey origin) against the
   HTTP.sys meaning; none has been checked yet.
3. **Startup failures (phase 136).** Recognise `HttpSysException` — access denied (no URL reservation) and
   prefix already registered (another process owns it) — and write the actionable text, including the
   `netsh` command for the first, to the console and the Event Log, as already done for a Kestrel port
   conflict.

## Verification

Windows only: install under a non-admin account with and without the reservation; start two instances on
the same prefix. Unit-test the URL validation and the exception-to-message mapping on any platform.
