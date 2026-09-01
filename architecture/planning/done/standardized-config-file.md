# Standardize configuration: a datasync.config.yaml in the repo root

**Resolved 2026-09-01, revised the same day.** Format is YAML, found by an explicit `--repo` or by
walking up from the current directory like git's own `.git` lookup. `serve` writes a starter file for a
fresh repo root. `Url` gets its own `DataSync:Url` key, still overridable by a CLI flag or environment
variable. `datasync invite`'s existing SQLite-only assumption — it doesn't handle `StateEngine: MsSql`/
`Postgres` at all today — is fixed as part of this rather than split out. `datasync service install` is
unchanged.

**Revision**: the file is git-tracked, not gitignored. A credential-bearing setting (`StateConnectionString`
with a password in it) is rejected outright, reusing `ConfigValidation.RejectEmbeddedCredential` —
already how `ConnectionConfig` handles this. Instead, a setting that needs a secret resolves it through
the same `SecretStore` connections already use, under a fixed, standardized ref
(`datasync:config:<key>`) rather than an admin-chosen one, settable with a new `datasync secret set`
CLI command. The starter file's comment names the exact ref and command for `StateConnectionString`.

The design is in `architecture/implementation/todo/phase-079-standardized-config-file.md`. The
admin-facing screen for viewing/editing this file is a separate, dependent phase —
`architecture/implementation/todo/phase-081-admin-config-screen.md`.
