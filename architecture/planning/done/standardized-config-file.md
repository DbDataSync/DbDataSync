# Standardize configuration: a datasync.config.yaml in the repo root

**Resolved 2026-09-01.** Format is YAML, gitignored by default (it can carry `StateConnectionString`
and other credential-bearing settings), found by an explicit `--repo` or by walking up from the current
directory like git's own `.git` lookup. `serve` writes a starter file for a fresh repo root. `Url` gets
its own `DataSync:Url` key, still overridable by a CLI flag or environment variable. `datasync invite`'s
existing SQLite-only assumption — it doesn't handle `StateEngine: MsSql`/`Postgres` at all today — is
fixed as part of this rather than split out. `datasync service install` is unchanged.

The design is in `architecture/implementation/todo/phase-079-standardized-config-file.md`.
