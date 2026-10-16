# Changelog

All notable changes to MediStock are recorded here, **newest first**.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html), with `MINOR` tracking the
milestone (`0.5.0` = Milestone 5). See [`plan.md`](plan.md) §8 for the versioning scheme.

Each released version records the **commit**, the **date**, and what changed. Dates are local
(India Standard Time, UTC+5:30). Entries are grouped as Added / Changed / Fixed / Removed /
Security, in that order.

---

## [Unreleased]

### Added

- **Stock-corrections domain — Milestone 4b groundwork** (`92fe8f6`, 2026-10-16) — the
  `SupplierReturn` aggregate: batches can only go back to the supplier that sold them, recalled
  and written-off stock is refused, repeated lines against one batch are counted together, and
  there is deliberately no reversal — a return that went out in error is corrected by receiving
  the goods again. `MedicineBatch.WriteOffRemainingStock` zeroes a batch's balance and sets its
  status in one step, so a quarantined batch can finally be disposed of. New `AdjustStock` and
  `ReturnToSupplier` permissions with the pharmacist and inventory-operator role decisions, three
  new audit actions (`StockAdjusted`, `StockWrittenOff`, `SupplierReturnRecorded`), the `SRET`
  number prefix, and ADR 0011. 372 domain tests.
- **GitHub Actions CI** — `.github/workflows/build.yml`: every push to `main` and every pull
  request builds the solution and runs all 653 tests on a `windows-latest` runner against its SQL
  Server LocalDB; pushing a `v*` tag additionally publishes the self-contained artefact and
  creates the GitHub release, reusing `publish-release.ps1` so CI and a local release cannot drift
  apart. See `docs/release-process.md` §3.

### Changed

- `publish-release.ps1` gained `-SkipTests`, so the CI release job can skip a test suite it has
  already run in its own test job.
- **Planning context moved into `docs/`** — `plan.md`, `CHANGELOG.md` and `currentstate.md` now
  live in `docs/` alongside the rest of the written record, so one folder holds everything a
  contributor or an agent needs for context. `docs/release-process.md` links updated.

### Fixed

- **Code-review findings** (`7d1d4c4`, 2026-10-16) — receipt stock movements are now booked under
  the operator-supplied received-on date instead of the entry day, so the ledger agrees with the
  batch and the reports; the return-refund preview prices multi-batch lines with the same
  clearing-remainder walk the domain uses, so the previewed refund is the refund that gets
  recorded; an empty cart no longer un-clears the last quote; the "today" date defaults on the
  sales-history, reports and returns screens are recomputed on every visit instead of being
  frozen at start-up; and the dashboard counts its low-stock and near-expiry figures directly
  instead of materialising two 500-row lists per refresh.

### Security

- **Sign-in timing** (`b5d9a55`, 2026-10-16) — the sign-in failure path now compares the supplied
  password against a per-instance decoy hash built with the production work factor, so a wrong
  user name costs the same time as a wrong password.

---

## [0.5.0] — Milestone 5: users, roles, audit and backup

**Commit** `d3afcf8` · **Date** 2026-10-16 · *Add users, roles, permissions, audit trail and backup*

106 files changed, +15,593 / −40.

### Added

- **Identity domain** — `Permission` (12 permissions), `Role` (Administrator, Pharmacist, Cashier,
  Inventory operator), `PermissionSet`, `RoleCatalog`, `PasswordPolicy`, `PasswordHash`,
  `User`, `AuditAction` (29 actions) and `AuditEntry`.
- **PBKDF2 password hashing** — `pbkdf2-sha256`, 210 000 iterations, per-account salt, encoded as
  `algorithm$iterations$salt$key`. Passwords are never stored, logged or displayed again.
- **First-run setup** — on an empty installation the sign-in window asks for the pharmacy's first
  administrator, creates the account, and signs it in.
- **Sign-in and sign-out** — a modal sign-in window, "sign out" in the sidebar, and the signed-in
  name and role shown in both the sidebar footer and the top bar.
- **Role-based permissions enforced in the application layer** — seven decorators
  (`MedicineCatalogAuthorization`, `SupplierCatalogAuthorization`, `PurchaseCatalogAuthorization`,
  `SaleCatalogAuthorization`, `SaleReturnCatalogAuthorization`, `UserDirectoryAuthorization`,
  `AuditTrailAuthorization`) registered through `CatalogAuthorization.AddGuardedCatalog<>`. A contract
  cannot be added without deciding its permission, because a test enumerates `GuardedContracts`.
- **Audit trail** — append-only `AuditLogs`, written *inside the same transaction as the change it
  describes* by all five mutating catalogues and by the identity services. Rows carry the actor, the
  acting account's id, the action, the affected record, a human summary, an optional reason and
  optional detail such as a backup path.
- **Users & Settings screen** — four tabs:
  - **Accounts** — search accounts (including deactivated), create an account, change a role, reset a
    password, deactivate/reactivate; a preview of exactly what the selected role may do.
  - **Audit trail** — filter the log by term, actor, action and date range; read a chosen row in full.
  - **Backup** — list, take, verify and restore backups of the live database.
  - **Support** — pharmacy, application and database summaries; check connectivity; apply pending
    schema changes; open the log folder; recent activity.
- **Backup and restore** — `SqlServerBackupService` writes timestamped `.bak` files through SQL
  Server's own `BACKUP DATABASE`, verifies them with `RESTORE VERIFYONLY`, and restores with
  `RESTORE DATABASE … WITH REPLACE` after forcing the application off the database.
- **Global exception handlers** — `DispatcherUnhandledException`, `AppDomain.UnhandledException` and
  `TaskScheduler.UnobservedTaskException` now log a full stack trace and tell the operator where the
  log is, instead of the process vanishing without a word.
- **`InstantTextConverter`** — renders every UTC instant in the operator's local time.
- **`ThemedTabControl` / `ThemedTabItem`** — the tab control used by Users & Settings.
- **`AuditText`** — one place that turns an `AuditAction` into the words shown to a person.

### Changed

- **MARS is forced off** on the application connection string. With multiple active result sets
  enabled, SQL Server refuses savepoints, so EF Core cannot roll back a partially-applied
  `SaveChanges` inside a transaction. Audited catalogues save twice in one transaction by design.
- **Purchases** now default *Received by* to the signed-in account.
- **Sales history and the dashboard** render timestamps in local time rather than raw UTC.
- **The dashboard footer** and its quick actions now point at the screens that exist (Returns,
  Users & settings) instead of promising later milestones.
- **Integration tests run as a signed-in administrator.** `TestDatabaseFixture` creates and signs in
  `integration.admin` through the real first-run path, so the authorization decorators see a genuine
  session rather than being bypassed for tests.
- **Repository gained** `plan.md`, `CHANGELOG.md`, `currentstate.md` and `docs/release-process.md`.

### Fixed

- **Audit rows were losing the actor.** `EfUserDirectory`, `EfMedicineCatalog` and `EfSupplierCatalog`
  called the two-argument `AuditJournal.Stage`, which defaults the actor to `system`; they had an
  `IUserSession` injected and unused. Every catalogue edit is now attributed to the real account, and
  the seeder stamps its own rows as `Development seed`.
- **A user could demote or reset themselves.** `EfUserDirectory` refused self-deactivation but not
  self-demotion or self-password-reset, so an administrator could lock the pharmacy out of its own
  administration screen. All three now refuse, with a message explaining why.
- **A fatal binding on a read-only property.** Two `TextBox`es were two-way bound to get-only
  properties, throwing `InvalidOperationException` out of the binding engine and killing the process
  during startup of the Users & Settings screen. Both are now `Mode=OneWay`.
- **A custom `TabControl` template blanked every tab body.** The template's content presenter was not
  named `PART_SelectedContentHost`, so `TabControl` never published `SelectedContent`. No error was
  raised: the tabs simply showed nothing.
- **The page cache was mutated off the UI thread.** `SignOutAsync` raises `IUserSession.Changed` on a
  thread-pool thread; the shell rebuilt the current page there, and its fire-and-forget refresh
  mutated bound `ObservableCollection`s with no dispatcher, crashing on sign-out. `ShellViewModel`
  now marshals through `Application.Current.Dispatcher`.
- **`reset-database.ps1` printed success after a failed migration.** It now checks the exit code and
  fails loudly, so a broken migration cannot masquerade as a rebuilt database.

### Security

- Passwords are hashed with PBKDF2-SHA256 and a per-account salt; the plaintext never leaves the
  password box.
- Sign-in refusals are logged with the account name but never the attempted password.
- The audit log distinguishes *"no such account"* from *"wrong password"* for an administrator
  reading the trail, while the interactive message is deliberately identical for both — the account
  name is not confirmed to an unauthenticated stranger.
- The application log was scanned for leaked secrets after a full UI exercise: zero matches.
- Backup and restore require the `BackupDatabase` permission; reading the audit trail requires
  `ViewAuditTrail`; managing accounts requires `ManageUsers`.

---

## [0.4.0] — Milestone 4: returns, expiry and reports

**Commit** `7e5dddf` · **Date** 2026-10-16 · *Add sales returns, expiry views and reporting*

72 files changed, +10,029 / −216.

### Added

- **Sales returns** — look up an invoice, cap every line at what is still returnable, preview the
  refund, require a reason and a per-line disposition (**restock**, **quarantine** or **destroy**),
  then write the return, the refund, the returned cost and any reversal movements in one transaction.
- **Return apportionment** — refunds and returned cost are spread across the original sale lines to
  the paisa with `Money.ProportionalShare` / `Money.RemainderShare`.
- **Restock policy** — returned stock only returns to the shelf when the operator chooses *restock*
  **and** the batch is still inside its expiry policy (`ExpiryPolicy.CanBeRestocked`). Quarantined and
  destroyed lines write no stock movement at all.
- **Returns & Adjustments screen** and **Reports screen**.
- **Ten operational reports** — daily sales, sales by medicine, sales by payment method, purchase
  history, stock by batch, low stock, expiring stock, stock movements, inventory valuation and sales
  returns — each filterable and exportable.
- **CSV export** — RFC 4180 quoting and a formula-injection guard, written to the configured report
  folder.
- **Returns projection and reporting read models** — `SalesReturnProjection`, `ReportTable`,
  `ReportDefinition`, `IReportQueries`, `ICsvExporter`.

### Changed

- `SaleLine` and `SaleLineBatchAllocation` gained `rowversion` tokens, closing a race in which two
  operators could refund the same units at the same moment.
- `RefundMethod` is constrained by `CK_Returns_RefundMethod_Range` (`BETWEEN 1 AND 4`).

### Fixed

- **A migration referenced a computed column.** `CK_SaleLines_ReturnedCost_Within_LineCost` compared
  against `[LineCost]`, which SQL Server cannot use in a check constraint (error 207). The constraint
  now compares the underlying columns directly.
- **`reset-database.ps1` reported success after a failed migration.**

---

## [0.3.0] — Milestone 3: point of sale

**Commit** `3d5af1c` · **Date** 2026-10-16 · *Add the point of sale: cart, FEFO checkout, invoices and receipts*

### Added

- **New Sale screen** — search or scan a medicine into a cart, see eligible batches, override the
  preferred batch, change quantities, remove lines, apply a discount, take one or more payments.
- **FEFO allocation** — first-expiry-first-out across as many batches as the sale needs, skipping
  expired, recalled, inactive and unavailable stock.
- **Transactional checkout** — allocating batches, pricing the lines, taking the invoice number,
  reducing the batches, writing the movements and recording the payments all happen in one
  transaction. A failed sale leaves nothing behind.
- **Invoice numbering** — `EfInvoiceNumberAllocator` uses a single atomic
  `MERGE … WITH (HOLDLOCK) … OUTPUT`, safe under concurrency and proven by six racing sessions.
- **Receipts** — `TextReceiptExporter` writes a plain-text receipt to the configured folder, and
  Sales History can reprint one.
- **Sales History screen** — search invoices by number, customer or medicine; the day's takings split
  by payment method; cancel an uncompleted invoice, reversing its stock in the same transaction.
- **Tax** — a per-medicine `TaxRatePercent` threaded from the catalogue through pricing to the
  invoice.

---

## [0.2.0] — Milestone 2: suppliers, purchasing and receiving

**Commit** `2cc1fa5` · **Date** 2026-10-16 · *Add suppliers, purchasing, receiving and inventory*

### Added

- **Suppliers** — create, edit, search, deactivate; a warning when an invoice number has already been
  received from that supplier, so the same delivery is not taken twice.
- **Purchases** — create as a draft, edit, receive into stock, reverse or cancel.
- **Receiving** — one batch and one stock movement per line, inside one transaction.
- **Inventory & Batches screen** — stock with sellable/non-sellable states, expiry filters, the full
  movement history, and a live reconciliation check that every cached balance equals its ledger total.
- **Stock movement ledger** — append-only, the source of truth for every quantity on hand.

### Fixed

- **Lifecycle writes were unconditional.** `UPDATE … WHERE [Id] = @p` let two operators both receive
  the same purchase, or both reverse it. A `rowversion` concurrency token plus a unit-of-work retry
  now makes the loser of the race reload and try again.
- **Business-date defaults used `DateTime.Today`** in the purchase editor, ignoring the configured
  time zone. `IClock` is now injected.

---

## [0.1.0] — Milestones 0 and 1: foundation and catalogue

**Commits** `a28f02c`, `b63510c` · **Date** 2026-10-16

### Added

- Solution and four source projects plus three test projects, with the dependency direction enforced.
- Dependency injection, layered configuration (committed defaults, development overrides, git-ignored
  local overrides, environment variable), and file logging.
- The WPF shell — sidebar navigation, top bar with a live connection indicator, placeholder pages.
- EF Core, the initial migration, and the `Medicines`, `MedicineBatches` and `StockMovements` tables.
- **Medicine catalogue** — search by name, brand, generic name or barcode; create, edit, deactivate;
  duplicate warnings; validation of units and pack sizes.
- **Dashboard** — today's sales, transaction count, low stock, near-expiry batches, recent movements.
- Development seed data: fictional medicines, suppliers, batches and movements.
- 152 tests across domain, application and integration projects.
- `README.md`, `docs/architecture.md`, `docs/database-design.md`, `docs/roadmap.md`, and decision
  records 0001–0005.

### Fixed

- Thirteen compounding defects found while building the first milestone, including money rounded with
  floating point, expiry dates compared in the wrong direction, and cached batch balances that could
  drift from the movement ledger.

---

[Unreleased]: https://github.com/ranababu1/medistock/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/ranababu1/medistock/releases/tag/v0.4.0
[0.3.0]: https://github.com/ranababu1/medistock/releases/tag/v0.3.0
[0.2.0]: https://github.com/ranababu1/medistock/releases/tag/v0.2.0
[0.1.0]: https://github.com/ranababu1/medistock/releases/tag/v0.1.0
