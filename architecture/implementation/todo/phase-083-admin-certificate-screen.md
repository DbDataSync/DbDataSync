# Phase 83 — the Certificates section of the Admin screen

**Status**: Planned, not started. Depends on phase 82 (the certificate operations themselves) and
phase 81 (the Admin section this lives inside) both landing first.
**Plan reference**: `architecture/planning/done/windows-tls-certificate-management.md`, resolved
2026-09-01.

## What this phase will build

A **Certificates** section in the Admin area, alongside phase 81's config table, gated the same way
(`[Authorize(Policies.Admin)]`, Admins only).

Phase 82 deliberately shipped complete without this — every operation is reachable from `datasync cert …`
— so this phase adds no capability. What it adds is **visibility**, and specifically visibility of the
two states an operator cannot otherwise discover until they cause an outage:

- **how long is left** on the bound certificate
- **whether the service account can actually read its private key**

The second is the one that matters. A missing read ACE on the private key produces a TLS handshake
failure at the *next restart*, saying nothing about permissions — so it is invisible for however long
the service happens to stay up, which in a replication engine can be months. Phase 82's daily check
raises a notification; this screen is where someone confirms it is fixed.

## The screen

**Current certificate** — subject, SANs, issuer, thumbprint, `NotAfter` with days remaining, and whether
it is self-signed or CA-issued. Days remaining is a status colour, not a date someone has to subtract
from today.

**Binding state** — whether `datasync.config.yaml` actually names this certificate, whether
`AllowInvalid` is set, and the restart-required banner (phase 81's, reused unchanged) when a change has
been made this session and the process has not restarted since.

**Private key access** — the service account name, and whether it can read the key. Three states, because
"unknown" is real: on a non-Windows host, or with no service installed, the question does not apply and
saying so is better than a green tick that means nothing.

**Actions**, each a door onto the phase 82 operation of the same name: `New self-signed`, `Enroll from
CA`, `Retrieve pending request` (shown only when a request is pending), `Renew now` — a re-enroll
carrying the bound certificate's subject and SANs — and `Bind` from the list of server-auth certificates
already in `LocalMachine\My`.

**The template field is a picker with a free-text fallback**, backed by phase 82's best-effort listing.
When the listing comes back empty it renders as a plain text input showing the reason phase 82 gave
("not domain-joined", "access denied on the configuration partition"). The field is never disabled and
enrollment is never blocked on discovery having worked — a picker that greys itself out when AD is
unreachable would turn a convenience into an outage.

**Platform and availability.** On a non-Windows deployment the section renders a single line saying
certificate management is Windows-only and TLS is expected to be terminated in front of the app. It is
not hidden — an admin looking for it should find out why it is absent rather than wonder whether they
lack a role.

## API surface

```
GET  /api/admin/certificate                  # bound certificate, expiry, binding state, key access
GET  /api/admin/certificate/candidates       # server-auth certs in LocalMachine\My, for Bind
POST /api/admin/certificate/self-signed
POST /api/admin/certificate/enroll
POST /api/admin/certificate/retrieve
POST /api/admin/certificate/bind
```

All `[Authorize(Policies.Admin)]`, matching phase 81's reasoning: even the read endpoint reveals the
deployment's hostnames, CA and certificate inventory.

**No endpoint returns a private key, and none offers export.** Phase 82 installs keys non-exportable;
this surface has no reason to weaken that, and the closest thing to a mistake available here would be an
"export for backup" affordance. There isn't one.

## How it will be verified

- `GET` reports the bound certificate's expiry and binding state against a seeded test certificate
- the key-access state renders as warning when the ACE is absent, ok when present, and *unknown* on a
  host where the question does not apply — all three, because the third is the one likely to be rendered
  as a false green
- a pending enrollment shows `Retrieve pending request` and hides it otherwise
- an empty template listing renders the field as free text with phase 82's reason shown, and enrollment
  still submits from it
- a Viewer-role session gets 403 from every endpoint, read included
- no response body contains key material, asserted directly rather than by inspection
- non-Windows renders the explanatory line rather than an empty section

Windows-dependent assertions carry `Category=Windows`, matching phase 82 — and the same honesty applies:
the AD CS enrollment action has no automated coverage, because the CA it needs does not exist in CI.

## Out of scope

- **Any operation phase 82 does not already expose.** This screen is a second door, not a wider one.
- **Certificate export or key backup.** Above.
- **Restarting the service from the screen.** A restart interrupts in-flight replication runs. The banner
  says one is needed; choosing the moment stays with the operator, as it does everywhere else in the app.
