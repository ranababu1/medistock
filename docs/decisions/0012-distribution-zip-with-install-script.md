# 0012 — Distribute a zip with a per-user install script, not MSIX or a setup wizard

**Status:** Accepted · 2026-10-16

## Context

Milestone 6 needs an installable release. The candidates were MSIX, a wizard-style `Setup.exe`
(Inno Setup / WiX), or the existing self-contained zip plus a small script.

## Decision

Keep the self-contained single-file zip as the artefact and ship `install.ps1` and `uninstall.ps1`
inside it.

- **MSIX** needs a code-signing certificate the project does not have; an unsigned package will not
  install on a normal machine. Revisit when a certificate exists.
- **A setup wizard** adds a build-time toolchain dependency (and CI installation of it) for little
  gain over a script on a single-PC, single-user product.
- The script installs per user to `%LOCALAPPDATA%\Programs\MediStock` (no administrator rights),
  adds a Start Menu shortcut, and starts LocalDB. On upgrade it keeps the operator's
  `appsettings*.json` (writing the shipped copy as `.new` if it differs).
- Uninstall removes application files and the shortcut only. The database, backups, receipts and
  logs are the pharmacy's records and are left in place; the script says where they are.

## Consequences

- Nothing here is signed, so Windows SmartScreen may warn on the downloaded exe.
- The SQL Server / LocalDB prerequisite is checked and reported, not installed.
- Install, upgrade and uninstall are verified into a temp folder; validating on a truly clean
  machine remains a manual step (`docs/release-process.md` §5).
