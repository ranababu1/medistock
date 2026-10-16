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

## Milestone 3 — Point of sale ✅

**Tasks**

- ✅ Build the fast product-search and cart interface.
- ✅ Implement eligible-batch allocation and FEFO.
- ✅ Implement pricing, discounts, and tax calculations.
- ✅ Implement checkout as a database transaction.
- ✅ Generate invoices and payment records.
- ✅ Add invoice printing/export.

**Acceptance criteria**

- ✅ A successful sale reduces the correct batch quantities.
- ✅ A failed sale leaves no partial sale, payment, or stock changes.
- ✅ Expired or insufficient stock cannot be sold.
- ✅ Invoice numbers remain unique.
- ✅ Currency and tax calculations pass boundary tests.

**Evidence:** `tests\MediStock.IntegrationTests\Sales\SaleCatalogTests.cs` drives the real workflow
end to end against SQL Server: a basket that spans two batches, a quote that agrees with its checkout
to the paisa, an expired batch that is skipped even when it expires soonest, a short basket that writes
nothing, a settlement that does not match the total, two tills racing for the last units, six tills
racing for invoice numbers, a preferred batch, invoice search by number and date range, the day's
takings moving by exactly one invoice, a reprint that changes no business state, and the same basket
sold twice being two genuine invoices. Unit tests in `MediStock.Domain.Tests` cover the pricing order,
rounding boundaries, discount limits and settlement, with no database. Four decisions shaped this
milestone: [ADR 0007](decisions/0007-point-of-sale-pricing-and-settlement.md) (pricing, allocation,
settlement and invoice numbering), [ADR 0002](decisions/0002-expiry-date-interpretation.md) (what
"expired" means at the till), [ADR 0006](decisions/0006-context-lifetime-and-execution-strategy.md)
(the batch rowversion that makes a quantity write conditional) and
[ADR 0003](decisions/0003-inventory-valuation.md) (why cost of goods sold is not valuation).

**Known gaps, deliberately left open:** a line is priced at the first allocated batch's selling price;
tax is one rate per line; and there are no percentage, basket-level or loyalty discounts. Returns,
cancellations and CSV report export were the gaps left over at the end of this milestone; all three
arrived in Milestone 4 — see [ADR 0008](decisions/0008-sales-returns-and-stock-disposition.md) and the
Milestone 4 evidence below. PDF and Excel exports remain unimplemented. A pharmacist must
confirm the discount, tax and pricing rules, and a tax professional the invoice format, before real
use — see the note at the end of ADR 0007.

---

## Milestone 4 — Returns, expiry, and reports ✅

**Tasks**

- ✅ Implement sale returns and reversals.
- ✅ Add near-expiry and expired stock views.
- ✅ Add low-stock alerts.
- ✅ Build sales, purchasing, stock, and valuation reports.
- ✅ Add CSV export.

**Acceptance criteria**

- ✅ Returns reference original transactions and respect returnable quantities.
- ✅ Restocking returned items requires an explicit disposition.
- ✅ Report totals reconcile with transaction data.
- ✅ Date-range boundaries and local business-date handling are tested.

**Evidence:** `tests\MediStock.IntegrationTests\Sales\SaleReturnCatalogTests.cs` takes medicine back off a
real invoice: a part return, the same units returned twice, an expired batch that may not be restocked,
a quarantined line that leaves the ledger alone, a return against a cancelled invoice, two tills racing
to refund the same unit, and the return number sequence. `tests\MediStock.Domain.Tests\Sales\SalesReturnTests.cs`
proves the arithmetic with no database — the counter's preview equals the recorded refund, and a line
split across several entries returns exactly its total, to the paisa.
`tests\MediStock.IntegrationTests\Reporting\ReportQueriesTests.cs` runs every one of the ten definitions and
checks each comes back as a drawable, self-describing table — a title, a subtitle saying what was asked for,
unique headers and rows exactly as wide as the columns claim — then reconciles the reports that carry money
with the transactions they summarise, including the day's takings moving by exactly one return.
`scripts\dev\check-reconciliation.ps1` grew three checks for the new tables and still prints `OK` for all
six: what a line says has come back must be what its return lines recorded (in units, refund, tax and
cost), a return's total must be its lines, and the restock movements must match the lines that say
`Restock` — nothing for a line that was quarantined or destroyed.

Returned money is shared proportionally, with the exact remainder on the entry that clears the line, so a
fully returned invoice refunds precisely what it charged. `Quarantine` and `Destroy` write no movement at
all, because the units never went back on the shelf. Both rules, and the reason a refund is not a
`Payment`, are recorded in [ADR 0008](decisions/0008-sales-returns-and-stock-disposition.md); what
"expired" means at the counter is still [ADR 0002](decisions/0002-expiry-date-interpretation.md), and the
valuation the reports print is still [ADR 0003](decisions/0003-inventory-valuation.md).

---

## Milestone 4b — Manual adjustments and expiry write-offs ⏳

Carried over from §3.3 of the plan: "Require a reason for manual adjustments and expiry write-offs."
This is split out of Milestone 4 rather than smuggled into it, because the workflow needs a pharmacist
to say what the reason codes are and what happens to written-off stock before the screen is worth
building.

**Tasks**

- Add an adjustment screen: increase or decrease a batch balance, with a mandatory reason.
- Add an expiry write-off that names the batch, the quantity and the reason, and leaves the history in
  place.
- Show quarantined stock as owned but unsellable, if the pharmacy wants it visible at all.
- Add supplier returns and the movement they create.

**Acceptance criteria**

- Every adjustment writes exactly one typed movement with a reason, and never edits or deletes an
existing one.
- Quantity on hand still reconciles with the ledger after any adjustment.
- A write-off cannot take a batch below zero, and cannot be recorded without a reason.

The movement types (`AdjustmentIncrease`, `AdjustmentDecrease`, `ExpiryWriteOff`), the reason
requirement and the stock-movement schema already exist and are tested; what is missing is the workflow
and its screen. Nothing in Milestone 5 depends on it.

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
