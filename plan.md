# MediStock — working plan

> This is the **living plan** for the repository: what we are building, in what order, and what
> "done" means for each piece. It is written to be read by a human *or* an AI agent picking the
> project up cold. It is updated at the end of every work session.
>
> Companion documents:
> - [`currentstate.md`](currentstate.md) — what the application actually does **today**.
> - [`CHANGELOG.md`](CHANGELOG.md) — what changed, when, and in which commit.
> - [`docs/roadmap.md`](docs/roadmap.md) — the original milestone brief with full acceptance criteria.
> - [`docs/decisions/`](docs/decisions/) — architecture decision records.

**Last updated:** 2026-10-16 · **Version:** 0.5.0 (see [`Directory.Build.props`](Directory.Build.props))

---

## 1. Product vision

MediStock is a Windows desktop application for pharmacy management: a medicine catalogue, batch-level
inventory, suppliers, purchasing and receiving, point-of-sale billing, returns, expiry tracking and
operational reporting, backed by Microsoft SQL Server.

It is both a learning project and the foundation of a genuinely useful small-business application.
The first release targets **one pharmacy on one Windows PC**, packaged as an installable executable.

### Guiding principle

> **Correctness and traceability first, convenience second, advanced features third.**

Every decision in this repository should be defendable against that sentence. When a feature can be
made faster by making it less traceable, it does not get made faster.

### Goals

- A polished, native Windows desktop application.
- Practical fluency in modern C#, .NET, WPF, XAML, SQL Server and Entity Framework Core.
- Pharmacy inventory modelled correctly at **medicine** and **batch** level.
- Sales and stock changes that are **transaction-safe** and **auditable**.
- Automated tests, logging, backups and a repeatable release process.
- A self-contained Windows release that installs and runs on a target PC.

### Explicit non-goals for the first release

Multi-branch synchronisation · cloud hosting · a web admin portal · mobile apps · insurance claims ·
clinical decision-making or medical advice · any claim of legal or regulatory compliance without a
dedicated review.

---

## 2. Technology

| Area | Choice | Notes |
|---|---|---|
| Language | Modern C# (`LangVersion latest`) | .NET SDK **10.0.402** |
| Runtime | .NET 10 | LTS |
| Desktop UI | WPF + XAML | `net10.0-windows` |
| UI architecture | MVVM, CommunityToolkit.Mvvm | source generators, no code-behind logic |
| Database | SQL Server — **LocalDB** for development | see [ADR 0004](docs/decisions/0004-localdb-for-development.md) |
| Data access | Entity Framework Core 10 + SQL Server provider | migrations only, never hand-edited schemas |
| Direct SQL | parameterised ADO.NET where EF gets in the way | invoice numbering, backup/restore |
| Validation | domain/application rules **and** database constraints | one never replaces the other |
| Logging | Microsoft.Extensions.Logging + a file provider | `%LOCALAPPDATA%\MediStock\logs` |
| Testing | xUnit | 286 tests at v0.4 |
| Source control | Git | `origin` = `github.com/ranababu1/medistock` |
| Packaging | `dotnet publish` self-contained + zip | attached to GitHub Releases |

### Layering

```
MediStock.Desktop  →  MediStock.Application  →  MediStock.Domain
MediStock.Infrastructure  →  MediStock.Application
```

`Domain` depends on nothing. Neither `Domain` nor `Application` references WPF or EF Core.

---

## 3. Milestones

| # | Scope | State |
|---|---|---|
| 0 | Environment, solution, DI, logging, shell, database connectivity | ✅ Complete |
| 1 | Schema, medicine catalogue, dashboard read models | ✅ Complete |
| 2 | Suppliers, purchasing, receiving, batch creation, stock movements | ✅ Complete |
| 3 | Point of sale — cart, FEFO, pricing, tax, transactional checkout, invoices, receipts | ✅ Complete |
| 4 | Returns and dispositions, cancellations, low-stock and expiry views, ten reports with CSV export | ✅ Complete |
| **5** | **Users, roles, permissions, audit trail, backup/restore, diagnostics** | 🚧 **In progress** |
| 4b | Manual stock adjustments, expiry write-offs, supplier returns, quarantined-stock visibility | ⏳ Planned |
| 6 | Packaging, installer, release process | ⏳ Planned |

---

## 4. Milestone 5 — the current sprint

**Goal:** the application is safe to put in front of a real pharmacy till. Named accounts, roles that
mean something, an audit trail that names the actor, a backup you can actually restore, and enough
diagnostics to support it.

### Work items

| Item | Scope | State |
|---|---|---|
| `m5-identity-domain` | `Permission`, `Role`, `PermissionSet`, `PasswordPolicy`, `PasswordHash`, `User`, `AuditAction`, `AuditEntry` | ✅ |
| `m5-app-identity` | `IAuthenticationService`, `IUserDirectory`, `IAuditTrail`, `UserSession`, drafts and DTOs, seven authorization decorators | ✅ |
| `m5-ef-identity` | `Users` / `AuditLogs` tables, EF configuration, `Pbkdf2PasswordHasher`, `EfUserDirectory`, `EfAuthenticationService`, `AuditJournal`, `EfAuditTrail` | ✅ |
| `m5-audit` | Audit staging wired into all five mutating catalogues + identity services, with the acting account | ✅ |
| `m5-backup` | `IBackupService` / `SqlServerBackupService` — list, create, verify (`RESTORE VERIFYONLY`), restore | ✅ |
| `m5-signin-ui` | First-run administrator setup, sign-in window, sign-out, shell operator strip | ✅ |
| `m5-users-ui` | **Users & Settings** screen: Accounts / Audit trail / Backup / Support tabs | ✅ built, being verified |
| `m5-users-audit` | Close the audit gaps: actor naming on every path, refusal rows, reason capture | 🚧 |
| `m5-diagnostics` | Structured logs, support diagnostics view, global exception handlers | 🚧 |
| `m5-tests` | Domain, application and integration tests for identity, audit, backup and recovery | ⏳ |
| `m5-docs` | ADR 0009 and 0010, `database-design.md` §3.14–3.15, `architecture.md`, `roadmap.md`, `README.md` | ⏳ |
| `m5-verify` | Build 0/0, full suite, rebuild the dev database, six reconciliation checks, UI smoke tests | ⏳ |
| `m5-commit` | Bump to 0.5.0, commit, tag, release | ⏳ |

### Acceptance criteria (from the brief)

- Unauthorized actions are blocked **in the application layer** and enforced where applicable at the
  data boundary.
- Sensitive operations are auditable, and the audit row names the account that did it.
- A backup can be restored successfully in a test environment.
- Errors are actionable and never expose credentials or sensitive information.

### Known defects found and closed during this sprint

Each of these was a real bug caught by review or by exercising the running application, not by a test
that already existed. They are recorded here because the *pattern* matters more than the instance.

1. **Lifecycle writes were unconditional** (`UPDATE … WHERE Id = @p`). Two operators could both
   receive the same purchase. *Closed in M2* with a `rowversion` concurrency token and a unit-of-work
   retry; extended to purchases, sales and returns in M3/M4.
2. **Audit rows lost the actor.** `EfUserDirectory`, `EfMedicineCatalog` and `EfSupplierCatalog`
   called the two-argument `AuditJournal.Stage`, which defaults the actor to `system` — so every
   catalogue edit was logged as if the machine had done it. *Closed in M5.*
3. **Self-targeted account administration.** `EfUserDirectory` refused to let an administrator
   deactivate themselves but happily let them demote themselves, which could lock everyone out.
   *Closed in M5.*
4. **Timestamps rendered as UTC on screen** while receipts printed local time. *Closed in M5* with
   `InstantTextConverter`.
5. **Collection modified from a thread-pool thread.** `SignOutAsync` runs with `ConfigureAwait(false)`
   and then raised `IUserSession.Changed` off the UI thread; the shell rebuilt the page and a bound
   `ObservableCollection` blew up. *Closed in M5* by marshalling in `ShellViewModel.OnSessionChanged`.
6. **Fatal XAML binding on a read-only property.** A `TextBox` two-way-bound to a get-only
   `BackupFolder` killed the process with no dialog. *Closed in M5*; the global exception handlers
   installed at the same time are what made it diagnosable.
7. **A custom `TabControl` template silently blanked every tab body** because the content presenter
   was not named `PART_SelectedContentHost`. No exception, no warning. *Closed in M5.*

---

## 5. Milestone 6 — packaging and release (next)

- [ ] Publish a self-contained `win-x64` single-file build.
- [ ] Decide installer vs. MSIX; document the LocalDB/SQL Server prerequisite on the download page.
- [ ] Add `-Setup.exe` naming, version stamping and release notes.
- [ ] Test install → upgrade → backup → restore → uninstall on a clean machine.
- [ ] Add a GitHub Actions workflow that builds, tests and attaches the artifact to a tag.

---

## 6. Backlog (after 1.0)

Manual stock adjustments and expiry write-offs · supplier returns · quarantined-stock view · PDF and
Excel exports · barcode label printing · purchase orders and approvals · supplier price history ·
catalogue import · multi-user over a LAN · multi-branch reporting · deterministic-rules-first AI
assisted search over product descriptions.

None of these start before the core is reliable.

---

## 7. How work is verified

Every increment follows the same loop. Do not skip a step because the change "looks small".

1. **Build clean** — `dotnet build MediStock.slnx`, zero warnings, zero errors.
2. **Test** — `dotnet test MediStock.slnx`, all tests green. New rules get new tests; a bug fix gets a
   test that fails without the fix.
3. **Rebuild the database from migrations** — `.\scripts\dev\reset-database.ps1`. If the schema
   changed, the migration must produce the schema from scratch, not just patch an existing database.
4. **Launch and look at it** — run the app and exercise the screen by hand or through UI Automation.
   A screen that compiles is not a screen that works.
5. **Reconcile** — `.\scripts\dev\check-reconciliation.ps1`. Cached balances must equal the ledger.
6. **Read the log** — `%LOCALAPPDATA%\MediStock\logs\medistock-*.log` must contain no `ERROR` or
   `CRITICAL` entries that were not deliberately provoked.
7. **Document** — update `CHANGELOG.md`, `currentstate.md`, the affected `docs/`, and add an ADR if a
   decision was made rather than a change.
8. **Commit** — small, imperative subject line, body explaining *why*, `Co-authored-by` trailer.

---

## 8. Release management

Versioning is **`0.MINOR.PATCH`**, where `MINOR` tracks the milestone:

| Version | Meaning |
|---|---|
| `0.1.0` … `0.4.0` | Milestones 0–4 complete |
| `0.5.0` | Milestone 5 — users, audit, backup |
| `0.6.0` | Milestone 6 — packaging |
| `1.0.0` | First release intended for a real pharmacy, after validation |

`PATCH` is bumped for fixes released between milestones.

The version lives in one place — `<Version>` in [`Directory.Build.props`](Directory.Build.props) —
and is stamped into the assembly, the window title and the artefact name.

```powershell
# build the release artefact and create a GitHub release
.\scripts\release\publish-release.ps1 -Version 0.5.0
```

See [`docs/release-process.md`](docs/release-process.md) for the full checklist, including the
manual steps that need a human (sign-in to GitHub, choosing the release notes, validating the
installer on a clean machine).

---

## 9. Conventions

- `decimal` for money — never floating point. Explicit midpoint rounding.
- Quantity is `decimal(18,3)`; money is `decimal(18,2)`.
- Timestamps are `DateTimeOffset` (UTC); business dates are `DateOnly` in the configured zone.
- Every stock change writes an append-only `StockMovements` row. **The ledger is the source of truth.**
- No hard deletes of transactional history — deactivate or reverse.
- Business rules live in testable domain/application code, never in views.
- Pass `CancellationToken`; keep transactions short; define the isolation strategy.
- Schema changes go through versioned migrations.
- Seed data is fictional and clearly separated from real data.
- Commit small, working increments.

---

## 10. Security and safety

- Least-privilege database access; no credentials or secrets in source, logs or the artefact.
- Destructive actions need confirmation and a reason.
- Reliable audit history for every stock and financial operation.
- Backups, and periodically **test restoring them**.
- Tax rules configurable and validated against current requirements.

> **MediStock is a learning project and is not certified, licensed or validated for clinical,
> regulatory or legal use.** Before real prescriptions, stock or money are involved, a qualified
> pharmacist must validate the medicine, prescription, returns, expiry and record-retention
> workflows, and the applicable licensing, drug-sale, tax-invoice, controlled-medicine and
> data-protection obligations must be confirmed with qualified professionals.
