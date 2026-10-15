# MediStock roadmap

The plan this project is built against: milestones in order, with the acceptance criteria that say
when each one is done. Update the state column as work lands; do not reorder the milestones.

**Guiding principle:** correctness and traceability first, convenience second, advanced features third.

---

## Milestone 0 — Environment and solution setup ✅

**Tasks**

- Verify Visual Studio workloads for .NET desktop development.
- Verify the installed .NET SDK.
- Verify the SQL Server engine and connection details in SSMS.
- Create the solution and projects.
- Configure Git and `.gitignore`.
- Add basic dependency injection and configuration.
- Add a simple application shell and logging.

**Acceptance criteria**

- ✅ The WPF application launches.
- ✅ The app can connect to the development database.
- ✅ Configuration and connection strings are not committed with secrets.
- ✅ The solution builds from a clean checkout.

---

## Milestone 1 — Database and medicine catalogue ✅

**Tasks**

- Define the first ER diagram and data dictionary.
- Implement EF Core configuration and the initial migration.
- Build medicine list, search, create, edit, and deactivate workflows.
- Add validation and error handling.
- Add domain and application tests.

**Acceptance criteria**

- ✅ Catalogue records persist across restarts.
- ✅ Duplicate or invalid data is handled clearly.
- ✅ Deactivated medicines remain available in historical records.
- ✅ Migrations can create a fresh development database.

---

## Milestone 2 — Suppliers and receiving inventory ✅

**Tasks**

- ✅ Implement suppliers.
- ✅ Implement purchase records and receiving.
- ✅ Create batch records from received goods.
- ✅ Implement stock movement history.
- ✅ Add stock balance reconciliation.

**Acceptance criteria**

- ✅ Receiving stock creates the correct batches and movements.
- ✅ Repeated submission cannot accidentally receive the same invoice twice without a clear warning or
  idempotency check.
- ✅ Stock balances reconcile with movements.
- ✅ Expiry dates and quantities are validated.

**Evidence:** `tests\MediStock.IntegrationTests\Purchasing\PurchaseCatalogTests.cs` covers the
receiving workflow end to end — including two receives racing for the same invoice, two reversals
racing for the same receipt, and a stale copy of a received purchase being written back — and
`scripts\dev\check-reconciliation.ps1` prints the live cached-versus-ledger totals for the development
database. A code review of this milestone found that a lifecycle transition was being issued as an
unconditional `UPDATE … WHERE [Id] = @id`, which let a losing writer commit over a winning one; the
remedy was a `rowversion` concurrency token on `Purchases` (migration
`20261015222213_PurchaseRowVersion`) and a single execution-strategy helper that turns the resulting
`DbUpdateConcurrencyException` into an operator-facing refusal. Three decisions shaped this milestone:
[ADR 0005](decisions/0005-no-purchase-orders-or-goods-receipts.md) (one purchase document, no goods
receipts),
[ADR 0006](decisions/0006-context-lifetime-and-execution-strategy.md) (context lifetime, the
execution-strategy contract, and conditional lifecycle writes) and
[ADR 0003](decisions/0003-inventory-valuation.md) (valuation).

---

## Milestone 3 — Point of sale ⏳

**Tasks**

- Build the fast product-search and cart interface.
- Implement eligible-batch allocation and FEFO.
- Implement pricing, discounts, and tax calculations.
- Implement checkout as a database transaction.
- Generate invoices and payment records.
- Add invoice printing/export.

**Acceptance criteria**

- A successful sale reduces the correct batch quantities.
- A failed sale leaves no partial sale, payment, or stock changes.
- Expired or insufficient stock cannot be sold.
- Invoice numbers remain unique.
- Currency and tax calculations pass boundary tests.

**Present state:** `FefoAllocator`, `BatchAvailability` and `ExpiryPolicy` are implemented and unit
tested. Pricing, tax, invoices and payments are not modelled — the plan requires a pharmacist to
confirm the real rules first.

---

## Milestone 4 — Returns, expiry, and reports ⏳

**Tasks**

- Implement sale returns and reversals.
- Add near-expiry and expired stock views.
- Add low-stock alerts.
- Build sales, purchasing, stock, and valuation reports.
- Add CSV export.

**Acceptance criteria**

- Returns reference original transactions and respect returnable quantities.
- Restocking returned items requires an explicit disposition.
- Report totals reconcile with transaction data.
- Date-range boundaries and local business-date handling are tested.

**Present state:** low-stock and near-expiry read models already back the dashboard.

---

## Milestone 5 — Users, security, and operational readiness ⏳

**Tasks**

- Add authentication and role-based permissions.
- Add audit logging for sensitive actions.
- Add backup and restore workflow.
- Add structured logs and a support diagnostics view.
- Test recovery from application crash and database interruption.
- Review secrets, access control, and sensitive data handling.

**Acceptance criteria**

- Unauthorized actions are blocked in the application layer and enforced where applicable at the
  data boundary.
- Sensitive operations are auditable.
- A backup can be restored successfully in a test environment.
- Errors are actionable and do not expose credentials or sensitive information.

---

## Milestone 6 — Packaging and release ⏳

**Tasks**

- Publish a self-contained Windows build.
- Decide on installer/MSIX packaging.
- Define how the database engine and database creation are handled on a new PC.
- Add versioning and release notes.
- Test installation, upgrade, backup, and uninstall scenarios.

**Acceptance criteria**

- The application starts on a clean target Windows machine that meets the stated prerequisites.
- The installer clearly documents any database engine prerequisite.
- Application upgrades preserve data and run database migrations safely.
- A release can be rebuilt from source using documented instructions.

---

## Cross-cutting work that is never "done"

- Automated tests at unit, integration and (later) end-to-end level.
- Logging that lets someone diagnose a failure from a user's PC.
- Auditability of stock and financial operations.
- Keeping credentials out of source, configuration and logs.
