# ClrKernel.Core.Secrets 0.9.2 → 0.11.0: a configurable prefix, and what it means for an existing install

**Resolved 2026-09-02, after the DataSync→DbDataSync rename (phase 92) landed.** New prefix:
`DbDataSync`, passed to `SecretStore`'s new constructor parameter at both construction sites. No
migration of already-stored secrets — an install re-enters credentials under the new prefix, the same
way it would set one for the first time. The package's own `SecretStore.EnvName`/`SecretPrefix` now
supersede this app's hand-rolled `SecretRefs.EnvironmentVariableFor`, which gets retired rather than
kept as a duplicate. The real 0.11.0 API (pulled from nuget.org and read directly, per this doc's own
"unverified" note) is transcribed into the phase doc.

The design is in `architecture/implementation/todo/phase-093-clrkernel-secrets-0.11-prefix.md`.
