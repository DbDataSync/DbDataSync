# Migrating a deployment from one passkey relying-party id to another

**Status: open, raw thought — not yet designed.** Split out of the phase 164 config-key-reorganization
work: that phase deliberately keeps `Auth:Passkeys:RelyingPartyId`/`RelyingPartyName` singular, exactly as
they are today, and this doc is where "how would an operator actually migrate it" lives instead.

## Why a config array alone doesn't solve this

The instinct — `Auth:Passkeys:RelyingParties: [{id, name}, ...]` instead of one id/name pair — looks like
it should let several relying-party ids be valid at once. It doesn't, on its own, because of how
verification actually works today (`PasskeyService.cs`):

- `PasskeyService` builds one `Fido2` instance with one `ServerDomain` (the configured
  `RelyingPartyId`), used for every ceremony — registration and assertion alike.
- Verifying an *existing* passkey's assertion (`CompleteAssertionAsync`) requires knowing which relying
  party id that specific credential was created under — that's cryptographically bound into the
  credential at creation time (the authenticator signs over the RP ID's hash), and the server must check
  the assertion against the matching id or verification simply fails. This is a hard WebAuthn constraint,
  not a limitation of this codebase.
- Nothing here records that today. `UserCredentials` stores only the credential id and public key
  (`PasskeyService.cs:105-108`, `Secret`/`Subject`) — no column names which relying party id a given
  credential was registered under. The whole system implicitly assumes every stored passkey was created
  under whatever `RelyingPartyId` the config currently says.

So a bare list of ids in configuration, with no other change, would be cosmetic: the moment
`RelyingPartyId` changed, every previously-registered passkey would stop verifying regardless of what
else is in that list — because nothing would know which id to check *that particular credential*
against.

## What real migration support would need

1. A new column on `UserCredentials` recording the relying-party id each passkey credential was actually
   registered under, at the moment it's created.
2. `PasskeyService.CompleteAssertionAsync` reading *that credential's own* recorded id when verifying,
   rather than the one global configured value — most likely constructing (or reconfiguring) the
   `Fido2Configuration` per verification rather than once at startup.
3. New registrations always targeting the **current primary** relying-party id and name — there is only
   ever one value a browser's passkey prompt shows during a *new* enrollment, so "name" doesn't pluralize
   the way "id" might.
4. A configuration shape that reflects that asymmetry rather than a flat peer list — plausibly something
   like today's singular `RelyingPartyId`/`RelyingPartyName` (the primary, used for new registrations)
   plus a separate list of legacy ids (ids only, no name) that stay accepted for verifying already-issued
   credentials during a transition window, and can eventually be retired once every user has re-enrolled.

## Open questions

- Is there operator-facing signal needed for "you have N credentials still on a legacy relying-party id,
  consider prompting those users to re-enroll"? Nothing today tracks per-user credential age/origin in a
  way that could drive that.
- Does dropping a legacy id from configuration need to also purge (or just stop honoring) the credentials
  that depend on it? Silently making them unusable without deleting the row could leave confusing
  "I have a passkey but it doesn't work" state in the Admin users screen.
- Whether `Fido2NetLib` supports constructing/verifying against a `Fido2Configuration` chosen per call
  cheaply, or whether this needs one `Fido2` instance per known relying-party id, cached — not checked
  against the library's real API here.
- Whether this is worth building at all before anyone has actually hit the need to change a relying-party
  id in production — plausibly this stays a documented gap (change the id, tell every user to re-enroll)
  until a real deployment asks for better.
