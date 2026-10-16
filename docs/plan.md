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
| Testing | xUnit | 710 tests at v0.6.0 |
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
| **5** | **Users, roles, permissions, audit trail, backup/restore, diagnostics** | ✅ **Complete — released as 0.5.0** |
| 4b | Manual stock adjustments, expiry write-offs, recall quarantine, supplier returns, quarantined-stock visibility | ✅ **Complete — released as 0.6.0** |
| **6** | **Packaging, installer, release process** | 🚧 **In progress** |

---

## 4. Milestone 4b — the current sprint

**Goal:** the three stock paths a pharmacy cannot live without and the original scope left open —
count corrections, expiry/damage disposal, recall holds, and medicine going back to the supplier.
**Released as `0.6.0` on 2026-10-16.**

### Work items

| Item | Scope | State |
|---|---|---|
| `m4b-domain` | `SupplierReturn` aggregate, `MedicineBatch.WriteOffRemainingStock`, `AdjustStock`/`ReturnToSupplier` permissions, `StockQuarantined` audit action, `SRET` numbering, ADR 0011 | ✅ |
| `m4b-app` | `IInventoryOperations` (adjust/write-off/quarantine), `ISupplierReturnCatalog` + draft, `StockCorrectionDrafts`, `GuardedInventoryOperations`, `GuardedSupplierReturnCatalog` | ✅ |
| `m4b-ef` | `EfInventoryOperations`, `EfSupplierReturnCatalog`, `SupplierReturns`/`SupplierReturnLines` configuration and migration, audit staging, rowversion-checked writes | ✅ |
| `m4b-inventory-ui` | Corrections strip on Inventory: difference-based count correction, whole-batch write-off (expiry/damage), recall and condemned quarantine holds, per-status hints | ✅ |
| `m4b-supplier-ui` | Supplier-return card on Returns & Adjustments: supplier picker, batch search, line add/merge, SRET recording, history list | ✅ |
| `m4b-quarantine-view` | Stock-held-in-quarantine read model on Returns & Adjustments | ✅ |
| `m4b-tests` | Domain, guarded-contract and integration tests — 710 green | ✅ |
| `m4b-verify` | Build 0/0, full suite, migration rebuild, reconciliation, **runtime UI verification of every flow via UI Automation** | ✅ |
| `m4b-release` | Bump to 0.6.0, commit, tag, release | ✅ `89f6ebf` + `v0.6.0`, pushed; artefact on the Releases page |

### Acceptance criteria (from the brief)

- Every correction writes its own typed stock-movement row with a reason; balances still reconcile
  against the ledger.
- Recalled and written-off stock cannot be sold, restocked or returned to the supplier, and the UI
  no longer offers actions the domain would refuse.
- Supplier returns leave stock and record the return in one transaction, under an atomically
  allocated `SRET` number, with credit at the price the goods entered at.
- The correction path is audited (`StockAdjusted`, `StockWrittenOff`, `StockQuarantined`,
  `SupplierReturnRecorded`) with the acting account inside the same transaction.

### Known defects found and closed during this sprint

1. **A fresh supplier-return line recorded quantity zero** — the line was created from the chosen
   quantity but the quantity was never assigned to it, so the first line showed `0` units and a
   `0.00` credit. Found by exercising the running screen.
2. **The "Returned by" box seeded the Windows account name**, not the signed-in operator, so a
   return by Ravi Menon was attributed to the machine login. Found by inspecting a live recorded
   return.
3. **"Adjust count" was clickable on a batch the domain refuses to move** — recalled and
   written-off batches are frozen by `MedicineBatch`, but the strip still offered the button, so an
   operator could walk the whole prompt flow only to be refused at the end. Found by following the
   designed next step (quarantine → write-off) through the UI.

Milestone 5's sprint record — work items, acceptance criteria and its seven known defects — now
lives in [`CHANGELOG.md`](CHANGELOG.md) under `0.5.0`, and in the docs as they stood at tag
`v0.5.0`.

---

## 5. Milestone 6 — packaging and release (in progress)

- [x] Publish a self-contained `win-x64` single-file build. *(done — `publish-release.ps1`, shipped
      as `v0.5.0`)*
- [x] Add a GitHub Actions workflow that builds, tests and attaches the artifact to a tag.
      *(`.github/workflows/build.yml` — build + test on every push/PR, release publish on `v*`
      tags, reusing `publish-release.ps1`)*
- [ ] Decide installer vs. MSIX; document the LocalDB/SQL Server prerequisite on the download page.
- [ ] Add `-Setup.exe` naming, version stamping and release notes.
- [ ] Test install → upgrade → backup → restore → uninstall on a clean machine
      (`docs/release-process.md` §5).

---

## 6. Backlog (after 1.0)

PDF and Excel exports · barcode label printing · purchase orders and approvals · supplier price
history · catalogue import · multi-user over a LAN · multi-branch reporting ·
deterministic-rules-first AI assisted search over product descriptions.

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
8. **Commit** — small, imperative subject line, body explaining *why*, no AI-attribution trailers;
   the configured Git author stays untouched.

---

## 8. Release management

Versioning is **`0.MINOR.PATCH`**, where `MINOR` tracks the milestone:

| Version | Meaning |
|---|---|
| `0.1.0` … `0.4.0` | Milestones 0–4 complete |
| `0.5.0` | Milestone 5 — users, audit, backup |
| `0.6.0` | Milestone 4b — stock corrections, supplier returns, quarantine |
| `0.7.0` | Milestone 6 — packaging and installer |
| `1.0.0` | First release intended for a real pharmacy, after validation |

`PATCH` is bumped for fixes released between milestones.

The version lives in one place — `<Version>` in [`Directory.Build.props`](Directory.Build.props) —
and is stamped into the assembly, the window title and the artefact name.

```powershell
# build the release artefact and create a GitHub release
.\scripts\release\publish-release.ps1 -Version 0.6.0
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
