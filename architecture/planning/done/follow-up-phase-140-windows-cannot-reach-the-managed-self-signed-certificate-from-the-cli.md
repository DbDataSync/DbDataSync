# On Windows, `config cert new-self-signed` cannot produce phase 130's managed certificate

**Status: fixed 2026-09-15** — see "Resolution" at the end. Extracted from
`architecture/implementation/done/phase-140-windows-ci-verification-and-remaining-failure.md`'s own
"Item 2b" section, where it was named as a real product gap and deliberately left alone — moved here per
`architecture/implementation/README.md`'s "Follow-up work gets its own doc, not a paragraph."

## The problem

`CertCommand.Run` dispatches on the OS before it dispatches on the subcommand. On Windows,
`new-self-signed` goes to the certificate-**store** implementation (phase 82): it issues into the Windows
certificate store and binds by subject. Off Windows it goes to `NewSelfSignedFile` (phase 130, tier 2):
it writes a managed PFX at `ManagedSelfSignedCertificate.PfxPath(root)` and points
`Kestrel:Certificates:Default:Path` at it.

So **there is no way to reach tier 2 from the CLI on Windows.** Not a bug in the file-based path — that
path is genuinely cross-platform (`CertificateBuilder`'s own doc comment says so, and
`SelfSignedCertificateServiceTests` exercises it on whatever OS runs them). It is a dispatch with no
Windows door to it.

## Why it matters

`DbDataSyncHost` will happily run tier 2's renewal service on Windows. Its registration is gated on the
configured certificate path matching the managed one and is **explicitly not OS-gated** — the comment
there says so outright, because "tier 2's whole point is a certificate story that works on Linux," not
that it is a Linux-only story:

```csharp
if (string.Equals(
        builder.Configuration["Kestrel:Certificates:Default:Path"],
        ManagedSelfSignedCertificate.PfxPath(certificateRepoRoot),
        StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHostedService<SelfSignedCertificateService>();
}
```

The result is a half-open door: the runtime supports the managed certificate on Windows, and the CLI
cannot create one there. A Windows operator who wants a self-renewing file certificate — rather than a
store-installed one requiring `bind` and, for the store, elevation — has to hand-edit
`dbdatasync.config.yaml` and place the PFX themselves, with nothing in `--help` suggesting that is even
possible.

How it surfaced: phase 140's Windows CI work, where `NewSelfSignedFileTests`' five tests failed on
`windows-latest` because they asserted a managed PFX that the command they invoked never set out to
write. Those tests are now `[NonWindowsFact]`, which is correct for them and leaves this gap unaddressed.

## Candidate directions, not evaluated

- **A flag** — `new-self-signed --file` (or `--store`, with the current behaviour as the default) making
  the choice explicit on both platforms, so the OS picks a default rather than the only option.
- **Follow the binding, not the OS** — if `Kestrel:Certificates:Default:Path` already names the managed
  path, renew in place regardless of platform; otherwise use the platform default. Matches how the host
  itself decides, which is the inconsistency that makes this a gap in the first place.
- **Leave it, and say so** — the store route really is the better Windows answer (OS-managed private key,
  ACLs, no PFX on disk), and tier 2 exists for platforms with no store. If that is the decision, the
  refusal should still be *stated* by the command rather than silently doing something else, and the
  Windows-side asymmetry documented in `docs/configuration.md`.
- Worth checking first whether anything else assumes tier 2 is reachable everywhere —
  `ReadinessChecks`' managed-certificate reporting is cross-platform (phase 140 made its tests
  platform-independent precisely because the check itself is), so at minimum the readiness output can
  describe a state Windows cannot currently get into.

## Resolution — 2026-09-15

The first candidate, chosen by the repo owner: **`new-self-signed` takes `--file` and `--store`.** The OS
now picks a *default* rather than being the only option.

- `--file` writes `<repo>/tls/dbdatasync.pfx` and points Kestrel at it, on any OS — so Windows can reach
  tier 2 from the CLI, which was the whole gap.
- `--store` forces the Windows certificate store, and off Windows refuses with a message naming why
  rather than quietly producing the other kind of certificate.
- No flag behaves exactly as before: the store on Windows, a file everywhere else. Nothing changes for
  anyone not asking for it, which is why this closed additively rather than by changing a default.

Two details worth keeping:

**`--file` deliberately does not require elevation.** The elevation guard added alongside this (see
`follow-up-phase-136-140-…`) refuses the store-writing subcommands when not Administrator, and
`new-self-signed` is one of them — but only when it is actually going to the store. The guard now tests
the resolved target rather than the subcommand name, so `new-self-signed --file` runs unelevated as it
should; it writes under the repo root and touches no machine store. Getting that wrong would have made
the new route unusable in exactly the situation it exists for.

**Verified by running it, not only by test.** On a real non-elevated Windows shell,
`config cert new-self-signed --file --days 30 --repo <tmp>` exits 0, writes the PFX, and
`config cert status` then reports `Bound certificate (file):` against it. The default (no flag) still
refuses without elevation, and `--file --store` together refuses as mutually exclusive.

Covered by `CertElevationTests`: the file route succeeds on Windows with no elevation override (run as
an operator would, so it has to work on its own), the default still takes the guarded store route, and
the conflicting-flags case refuses. The usage text documents both flags and which is the default where.

The doc's last bullet — that `ReadinessChecks` could describe a state Windows could not reach — resolves
itself: Windows can now reach it.
