# Phase 197H — Certificates under HTTP.sys

**Status**: Not started. Follow-up to phase 196H; the largest gap between Kestrel and HTTP.sys hosting.

## Why

Under `App:Server=httpsys` every certificate feature is quietly inert. `cert bind`, `use-pem`, `use-pfx`,
`new-self-signed`, the readiness check, the Admin certificate screen, the TUI tab and `CertificateExpiryService`
all read or write `Kestrel:Certificates:Default:*`, which HTTP.sys never reads. An operator following the
existing flow gets a service that starts, logs a warning, and serves no https. Until this is closed, HTTP.sys
cannot sensibly become the default.

## What HTTP.sys needs instead

A certificate is bound to an `ip:port` or `hostname:port` in the OS (`netsh http add sslcert`, or the
HttpApi `HttpSetServiceConfiguration` call), by thumbprint from the machine store, with an application id. It
can be changed without restarting the process. PEM and PFX files cannot be bound directly; they have to be
imported into the store first.

## Decisions to make

1. **One abstraction or two flows.** Preferred: a binding interface in `DbDataSync.Certificates` with a Kestrel
   implementation (today's config keys) and an HTTP.sys implementation (store + sslcert binding), selected by
   `App:Server`, so the commands and screens ask "bind this certificate" and do not know which.
2. **netsh or interop.** Shelling out to `netsh` is simplest and matches `sc.exe` use elsewhere; HttpApi interop
   avoids parsing text. Start with netsh unless parsing proves fragile.
3. **Where the binding lives.** The sslcert binding is OS state, not git-tracked config. `cert status` has to
   read it back from the OS, and `dbdatasync.config.yaml` should record only what is needed to re-create it.
4. **File-based certificates.** `use-pem` and `use-pfx` become import-then-bind. Decide who owns the imported
   store entry (removed when replaced?) and where the PFX password goes, given DbDataSync persists none today.
5. **Managed self-signed (phase 130).** `SelfSignedCertificateService` renews a file; under HTTP.sys it would
   have to re-import and rebind.

## Scope

- Binding abstraction and both implementations; `cert` subcommands, readiness checks, Admin screen and TUI tab
  go through it.
- `CertificateExpiryService` and `cert status` report on the active binding.
- The "ignored Kestrel certificate keys" startup warning becomes unnecessary for the cases that now work.
- Docs: the HTTP.sys section of `docs/configuration.md` loses its certificate row.

## Verification

Windows only, on a real host: bind, replace and remove a certificate with the service running; confirm https is
served with the new certificate without a restart; expiry notifications follow the bound certificate.
