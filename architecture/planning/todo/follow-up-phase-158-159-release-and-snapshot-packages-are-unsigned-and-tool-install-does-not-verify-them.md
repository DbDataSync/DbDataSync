# Release and snapshot packages are unsigned, and `dotnet tool install` would not check a signature if they were

**Status: open. Investigated 2026-09-19 (tests against the real published package, and the current NuGet and GitHub
docs); nothing built.** Extracted from `architecture/implementation/todo/phase-159K-automated-update-from-cli-and-web-console.md`
and `phase-158K-snapshot-releases-and-cli-update-staging.md`, per `architecture/implementation/README.md`'s "Follow-up
work gets its own doc, not a paragraph." Written while checking a claim both phase docs made and that turned out to be
wrong: that installing from nuget.org gives a signature check "in the bargain".

## Why it matters

Phase 159 lets a running service arrange for root to install a new version of itself. Its trust boundary limits what a
**compromised service** can do to "install a genuine release" — the privileged step re-derives everything and refuses
anything not in the pinned sources. It does nothing about a compromised **release pipeline or publishing account**: a
malicious package published as a real release, or a snapshot attached to a real GitHub release, is by construction
"genuine" as far as the pinned sources go. Package signing is the control for that, and today we have none of it.

The weakest point is snapshots: their only integrity check is a SHA-512 sidecar published in the same release as the
file, which catches a corrupted download and nothing else.

## What is true today (tested)

- **nuget.org's copy** of `DbDataSync` (2026.9.18.1918) carries a **repository** signature: `CN=NuGet.org Repository by
  Microsoft`, SHA-256 `1F4B311D9ACC115C8DC8018B5A49E00FCE6DA8E2855F9F014CA6F34570BC482D`, valid 2024-02-22 to
  2027-05-18. nuget.org adds this to every package it hosts.
- **The copy attached to the GitHub release is unsigned** (`dotnet nuget verify` → `NU3004`). `release.yml` uploads the
  pre-signing original. Snapshots are built the same way and are unsigned too.
- **We publish no author signature.** Publishing uses nuget.org Trusted Publishing (phase 127), which protects the
  *credential* — no long-lived API key — and is not a signature on the package.

## What does not work: NuGet's trust policy for tools

`signatureValidationMode=require` with `trustedSigners` (author or repository certificate, fingerprint, `owners`) is
NuGet's documented way to say "only install packages signed by these people". On SDK 10.0.112 / Linux:

| command | package | policy | result |
| --- | --- | --- | --- |
| `dotnet restore` (fresh package folder) | real, nuget.org-signed | `require`, **wrong** trusted certificate | **fails**, `NU3034` |
| `dotnet tool install` | real, nuget.org-signed | `require`, **wrong** trusted certificate | **installed** |
| `dotnet tool install` | **unsigned**, from a folder | `require`, nuget.org certificate trusted | **installed** |

The tool result was the same with `DOTNET_NUGET_SIGNATURE_VERIFICATION=true`. A restore against a package folder that
already held the package looked as if it also skipped the check — verification happens at download, so a test needs a
fresh package folder. This matches a public report, dotnet/sdk #37469 ("`dotnet tool update` skips NuGet package
signature verification"). So adding `trustedSigners` to the pinned `nuget.config` the privileged step runs `dotnet`
with would **look** like a control and be none. Do not do that and call it done.

## What does gate, with stock tooling

`dotnet nuget verify --all --certificate-fingerprint <sha256> <file>` checks the file itself and reports through its
exit code (all three tested against real files):

| file | pinned to | exit | message |
| --- | --- | --- | --- |
| nuget.org's copy | nuget.org's certificate | 0 | `Signature type: Repository` |
| nuget.org's copy | a different certificate | 1 | `NU3034` — "did not match any of the allowed certificate fingerprints" |
| the unsigned GitHub-release copy | nuget.org's certificate | 1 | `NU3004` — "not signed" |

So the privileged step can do the checking itself instead of trusting `dotnet tool` to: download the nupkg into its
own private directory (it already does this for snapshots), run `dotnet nuget verify` pinned to a fingerprint, and
install **only from that folder** — no custom cryptography, no new dependency.

## Options

**A. Pin nuget.org's repository certificate** (stable and beta only). Stops a tampered mirror or a middlebox. Does
*not* stop a compromised nuget.org account or publishing token — nuget.org would counter-sign whatever was uploaded.
The certificate rotates (this one expires 2027-05), and a service index announces successors, so a pinned value needs
maintaining. Cheap, weak.

**B. Author-sign our packages, and pin our own certificate.** Sign in `release.yml` and `publish-snapshot.yml` with a
code-signing certificate; register it on nuget.org; attach the **signed** nupkg to the GitHub release (not the
pre-signing original); pin its fingerprint in the privileged step. The only option that survives a compromised
publishing token or account, and the only one that covers snapshots with stock tooling — a signed nupkg verifies
identically whether it came from nuget.org or from a folder. Requirements (from the NuGet docs): RSA ≥ 2048 with the
code-signing purpose, an RFC 3161 timestamp, a chain to a root trusted by default — **nuget.org rejects self-signed
certificates**. Ways to get one: a purchased certificate held as a CI secret (which reintroduces the long-lived
credential phase 127 removed), or **Azure Trusted Signing**, which the Sign CLI (`dotnet/sign`) supports for NuGet and
which can authenticate from GitHub Actions through OIDC federation, so no stored secret. **Eligibility and cost were not
confirmed** — that is the first thing to check.

**C. GitHub artifact attestations** (`actions/attest`, Sigstore). Prove *which workflow at which commit* produced a
file, which is precisely what a snapshot's checksum cannot. `gh attestation verify` checks them. But there is no
`dotnet nuget verify` equivalent, and a server does not have `gh`; the tool itself would need a Sigstore bundle
verifier (bundle and certificate-chain checks against Sigstore's roots, offline-capable). That is real code and a real
dependency. Better as a check a human or CI runs than as this tool's own gate, unless we decide to build the verifier.

**D. Immutable releases.** Lock a release's tag and assets once published and generate a release attestation. The tag
can never be reused, which is fine for uniquely named snapshot tags, but snapshot retention *deletes* the release and
its tag (`gh release delete --cleanup-tag`); GitHub's docs say the tag cannot be deleted while the release exists and do
not say what deletion does. Needs an experiment before it is switched on for snapshots. Reasonable for stable releases.

## Recommendation

**B, for stable, beta and snapshots** — sign once, verify the same way everywhere — with **Trusted Signing over OIDC** as
the thing to evaluate first. If it is not available to this project, the honest alternative is a purchased certificate,
and the decision is then whether one long-lived signing credential in CI is a better trade than none. **A** is a stopgap
that should not be presented as protection against the case that matters. **C** is worth adding to `publish-snapshot.yml`
regardless — it is one action and buys an auditable link from a snapshot to its commit — but as a human-facing check
until there is a verifier.

## Changes this would make

- **The privileged step** (`UpdateApplier.ApplyPendingAsync`): for *every* channel, download the nupkg itself (nuget.org's
  flat container for stable/beta; the GitHub asset for a snapshot), `dotnet nuget verify --all
  --certificate-fingerprint <pin>`, refuse on a non-zero exit, and install with a config whose only source is that
  folder — today a snapshot's `--add-source` *adds* to nuget.org rather than replacing it, and stable goes through
  nuget.org directly.
- **Where the pin lives**: compiled into the binary, or in a root-owned file. **Never** in a setting the service can
  write (`dbdatasync.config.yaml`, the Configuration screen) — the whole point of phase 159's boundary is that root
  does not act on anything the service could have forged. A compiled pin also ties trust to a release, so rotating a
  certificate means shipping a build that trusts both for a while.
- **`dbdatasync update`** (the CLI's own staging and plan) and `update --apply` should verify the same way.
- **The pipelines**: signing in `release.yml` and `publish-snapshot.yml`, the signed file uploaded to the GitHub release,
  the `.sha512` sidecar recomputed over the signed bytes (or dropped, since the signature supersedes it).
- **Docs**: `docs/install.md`'s trust wording, which currently says a snapshot's checksum catches corruption "not
  tampering" — still true, but the sentence should say what replaces it.

## Open questions

- **Is Azure Trusted Signing available and affordable for this project**, and does its short-lived certificate model
  (a new certificate every few days) fit a **pinned fingerprint**? A pin to a leaf that changes every few days does not
  work; the pin would have to be to the CA or the signing identity's subject, which `dotnet nuget verify` may not
  express. This could rule Trusted Signing out for *pinning* even if it works for *signing*.
- **Does `dotnet nuget sign` accept a Trusted Signing certificate from Linux CI**, or is `dotnet/sign` (and Windows) the
  only route? Not tested — a self-signed test certificate failed chain validation (`NU3018`), so the sign → verify flow
  has not been demonstrated end to end here at all.
- **Downgrade and rollback.** A version older than the signing rollout is unsigned, so a pin that refuses unsigned
  packages refuses to roll back to it. The rollback package is the *installed* one (kept from the tool store), so it
  needs its signature checked when it was installed, or an explicit exception.
- **Timestamp authority.** NuGet wants an RFC 3161 timestamp so a signature outlives its certificate; that is a
  network dependency in CI and one more thing to pin or trust.
- **Does GitHub's immutable-releases feature interact with `--cleanup-tag`?** Needs an experiment on a scratch
  repository, not a guess.
- **Is a Sigstore verifier worth building** (option C as the tool's own gate), given `gh` cannot be assumed on servers?

## Addendum (2026-09-21, phase 163): the container image is a third artifact with the same question

Phase 163 publishes `ghcr.io/dbdatasync/dbdatasync` (amd64 and arm64). It is not signed either, and unlike a NuGet package there is
no equivalent of "the client would not check anyway" to fall back on for the *pull* side: `docker pull` verifies digests, but not who
produced them.

What is and is not there today:

- **Provenance attestations are on** (buildx's default when pushing by digest), so each per-architecture image carries an in-toto/SLSA
  attestation manifest saying which workflow built it. That is a claim stored beside the image, not a signature anyone checks by
  default; `imagetools inspect` shows the `unknown/unknown` entries that hold it.
- **No image signature.** Nothing is `cosign`-signed and no GitHub artifact attestation is created for the image digest.
- **The trust root is the same as for the snapshots**: TLS to the registry and who can push to the repository. A tag is mutable — `latest`
  moves by design, and even a version tag can be replaced (`overwrite=true` on `publish-image.yml`, which exists for retries).

This belongs in the same decision as options A–D above rather than beside it: if the release pipeline gains a signing identity for the
package (option B) or GitHub attestations (option C), the image should be signed with the same identity in the same job, using the
image **digest** the `merge` job already has. GitHub artifact attestations (`actions/attest-build-provenance`, `subject-name` = the image
and `push-to-registry`) would give the image a verifiable claim with no key to manage, matching option C. Not done, and not asked for yet
— recorded so the image is not forgotten when the package's answer is chosen.

Open: does anyone verify an image's attestation before running it? `docker pull` does not, and `gh attestation verify oci://…` needs `gh`
on the pulling machine; a Kubernetes admission policy (or `cosign verify`) would be the enforcement point, and is the operator's, not this
project's.

## Sources

NuGet: *Manage package trust boundaries* and *Signed Packages* (learn.microsoft.com/nuget); dotnet/sdk #37469; GitHub
docs on artifact attestations and immutable releases; `dotnet/sign`; the Trusted Signing GitHub Action. The tests above
were run on 2026-09-19 with `dotnet` SDK 10.0.112 on Linux against `DbDataSync` 2026.9.18.1918.
