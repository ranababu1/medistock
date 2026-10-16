# MediStock — current state

> **What the application actually does right now**, as opposed to what it is meant to do
> ([`plan.md`](plan.md)) or how it got there ([`CHANGELOG.md`](CHANGELOG.md)).
>
> Read this first when picking the project up. It is written to be honest about what is finished,
> what is half-finished, and what is known to be broken.

**Snapshot taken:** 2026-10-16 · **Version:** 0.5.0 (unreleased) · **Branch:** `main` ·
**HEAD:** `7e5dddf` (Milestone 5 is uncommitted working-tree changes on top of it)

---

## 1. One-paragraph summary

MediStock is a working WPF desktop application. It opens, connects to a SQL Server LocalDB instance,
applies its own migrations, signs an operator in against a real account with a hashed password,
and then offers a complete pharmacy workflow: maintain a medicine catalogue, keep suppliers, raise
and receive purchases that create batches and stock movements, sell at the counter with first-expiry
allocation and transactional checkout, return goods with a disposition, and export any of ten
operational reports to CSV. Everything that moves stock or money is written to an append-only ledger
inside the same transaction as the change itself, every sensitive action is written to an audit
trail naming the account that did it, and the database can be backed up, verified and restored from
inside the application. It has 286 automated tests, of which 77 run against a real SQL Server.

---

## 2. Environment this was built and verified on

| Thing | Value |
|---|---|
| OS | Windows 10 Pro |
| IDE | Visual Studio Community 2026, 18.11.0 |
| .NET SDK | **10.0.402** |
| Desktop runtime | 10.0.13 |
| Database | **SQL Server 2025 Express LocalDB 17.0.4025.3**, instance `MSSQLLocalDB` |
| `sqlcmd` | `C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE` |
| `dotnet-ef` | 10.0.13 |
| Time zone | India Standard Time (UTC+5:30) |
| Repository | `C:\Users\Imran\source\repos\MediStock` |
| Solution file | **`MediStock.slnx`** — the .NET 10 XML solution format, **not** `.sln` |
| Output path | `src\MediStock.Desktop\bin\Debug\net10.0-windows\MediStock.Desktop.exe` |

> The LocalDB instance must be started before the application can connect:
> `sqllocaldb start MSSQLLocalDB`. `scripts\dev\start-localdb.ps1` does this and proves it answers.

---

## 3. What works today, screen by screen

### Shell

Sidebar navigation with ten entries, a top bar showing the pharmacy name, the local time zone, the
signed-in operator and the database connection state, and a main content region. Pages are resolved
from a route key and cached; signing out clears the cache and returns to the sign-in window.

### Sign-in and first run

On an installation with no accounts, the sign-in window switches to a first-run form asking for the
first administrator's name, display name and password, creates the account, and signs it in. On a
provisioned installation it asks for a user name and password, refuses politely on a mismatch, and
signs the operator in. The password policy requires at least ten characters and refuses a password
that contains the account name. The password box clears itself when the window closes.

**Sign-in is enforced**: every other screen is behind it, and the shell shows who is signed in.

### Dashboard

Today's sales total and transaction count, the low-stock list, batches nearing expiry, recent sales
and recent stock movements, plus quick actions for New Sale, Receive Purchase, Search Medicine,
Reports, Returns and Users & settings. Every figure comes from a query the operator can trace.

### Medicine catalogue

Search by display name, brand, generic name or barcode. Create, edit and deactivate. Duplicate
warnings, validation of strength, dosage form, pack size, reorder threshold and tax rate. Deactivated
medicines stay in the catalogue and in history — nothing is ever deleted.

### Suppliers

Search, create, edit and deactivate. Supplier invoice references are recorded on purchases, and the
application warns when an invoice number has already been received from the same supplier, so the
same delivery is not taken into stock twice.

### Purchases

Create a purchase as a draft, edit it, receive it into stock, reverse a received purchase, or cancel
a draft. Receiving creates one batch and one stock movement per line inside a single transaction.
Partial receiving is supported. **Every lifecycle transition is a conditional write against a
`rowversion` token**: two operators acting on the same purchase at the same moment cannot both take
the stock in — the loser is told to reload and try again.

### Inventory & batches

Stock listed by medicine and batch with sellable / non-sellable state, expiry filters, and a full
movement history. A live reconciliation reports whether every cached batch balance still equals the
sum of its movements.

### New Sale (point of sale)

Search or scan a medicine into a cart. Eligible batches are shown with their expiry dates; the
preferred batch can be overridden. Quantities can be changed and lines removed before checkout.
The catalogue tax rate is applied, one or more payments can be taken, and an invoice number is
allocated atomically. Allocating batches, pricing, taking the number, reducing stock, writing the
movements and recording the payments happen in **one transaction** — a failed sale leaves nothing
behind. Expired, recalled, inactive and short stock is refused outright.

### Sales History

Search invoices by number, customer or medicine. Reprint a receipt. See the day's takings split by
payment method. Cancel an uncompleted invoice, which reverses its stock in the same transaction and
keeps the invoice number on record.

### Returns & Adjustments

Look up an original invoice, see what is still returnable per line, preview the refund, and require a
reason plus a per-line disposition: **restock**, **quarantine** or **destroy**. Returned stock only
goes back on the shelf when the operator chooses *restock* **and** the batch is still inside its
expiry policy. Refunds and returned cost are apportioned across the original lines to the paisa.

### Reports

Ten reports — daily sales, sales by medicine, sales by payment method, purchase history, stock by
batch, low stock, expiring stock, stock movements, inventory valuation and sales returns — rendered
as filterable tables with totals, and exportable to RFC 4180 CSV with a formula-injection guard.

### Users & Settings *(Milestone 5 — built, being verified)*

Four tabs:

- **Accounts** — search accounts including deactivated ones; create an account, change a role,
  reset a password, deactivate or reactivate; a preview of exactly what the selected role may
  do. The acting administrator **cannot** deactivate, demote or reset their own account.
- **Audit trail** — filter by term, actor, action and date range; read a chosen row in full,
  including the reason it was given.
- **Backup** — list backups with their size, take a new one, verify an existing one with
  `RESTORE VERIFYONLY`, and restore.
- **Support** — pharmacy, application and database summaries; check connectivity; apply pending
  migrations; open the log folder; the most recent audit rows as "recent activity".

### Roles

| Role | May do |
|---|---|
| Administrator | everything, including managing users, backing up and reading the audit trail |
| Pharmacist | everything except managing users, backing up and reading the audit trail |
| Cashier | sell |
| Inventory operator | manage medicines and suppliers, receive purchases, run reports |

Permissions are enforced by decorators around the catalogue contracts, so a screen cannot reach the
database without passing a permission check — and an application test enumerates every guarded
contract so a new one cannot ship unguarded.

---

## 4. Database

**Seven migrations**, applied automatically at startup when `ApplyMigrationsOnStartup` is true:

```
20261015214039_InitialCreate
20261015215632_PurchasingAndReceiving
20261015222213_PurchaseRowVersion
20261015223056_PointOfSale
20261015224739_MedicineTaxRatePrecision
20261015225936_SalesReturnsAndStockDisposition
20261016050304_UsersRolesAndAuditLog
```

The development database is `MediStock` on `(localdb)\MSSQLLocalDB`; the integration test database is
`MediStock_Test`, created, migrated and dropped by the test fixture.

A healthy freshly-seeded development database contains **10 medicines, 4 suppliers, 3 user accounts,
8 purchases, 13 purchase lines, 11 batches, 19 stock movements, 3 invoices, 6 sale lines,
6 allocations, 4 payments, 1 sales return of 2 lines**, an inventory value at cost of **₹7,378.20**,
and **₹489.02** of takings on record.

---

## 5. Configuration

Layered, later wins:

1. `src\MediStock.Desktop\appsettings.json` — committed defaults, no secrets.
2. `appsettings.Development.json` — committed, applied in `DEBUG`; enables seeding.
3. `appsettings.Local.json` — git-ignored machine overrides.
4. `MEDISTOCK_CONNECTION` — environment variable, highest priority for the connection string.

| Key | Default | Meaning |
|---|---|---|
| `PharmacyName` | `MediStock Pharmacy` | printed on invoices and in the shell |
| `TimeZoneId` | `India Standard Time` | used to derive the local business date |
| `NearExpiryDays` | `90` | near-expiry window |
| `ApplyMigrationsOnStartup` | `true` | apply pending migrations at launch |
| `SeedDevelopmentData` | `false` (true in Development) | seed fictional data when the catalogue is empty |
| `ReceiptFolder` | `MediStock\Receipts` | relative paths resolve under the user's documents folder |
| `ReportFolder` | `MediStock\Reports` | as above |
| `BackupFolder` | `MediStock\Backups` | as above |
| `PasswordIterations` | `210000` | PBKDF2 iterations |

**MARS is forced off** on the connection string regardless of what is configured, because with
multiple active result sets enabled SQL Server refuses savepoints and EF Core cannot roll back a
partially-applied `SaveChanges` inside a transaction.

Logs: `%LOCALAPPDATA%\MediStock\logs\medistock-yyyyMMdd.log`.

---

## 6. Development accounts

Created by the seeder behind `MediStock:SeedDevelopmentData`, so they exist in a development database
and nowhere else. The seeder signs out when it finishes, so the application always starts at the
sign-in window.

| Account | Display name | Role | Password |
|---|---|---|---|
| `admin` | Ravi Menon | Administrator | `Kestrel-Harbour-71` |
| `pharmacist` | Dr Sameer Kulkarni | Pharmacist | `Fennel-Juniper-38` |
| `cashier` | Anita Rao | Cashier | `Marigold-Till-92` |

Integration tests create their own administrator, `integration.admin` / `Tessellate-Meadow-42`,
through the real first-run path.

---

## 7. Tests

| Project | Covers | Count |
|---|---|---|
| `MediStock.Domain.Tests` | money rounding and proportional shares, guards, medicine rules, expiry policy, FEFO ordering, batch balances and restock eligibility, movements, purchase lifecycle, sale pricing and settlement, returns and refund estimation, **roles, permissions, password policy, first-administrator rules** | 191 |
| `MediStock.Application.Tests` | DTO and draft normalisation, `OperationResult` semantics, **the guarded-contract inventory and decorator refusals** | 18 |
| `MediStock.IntegrationTests` | migrations, schema constraints, catalogue, suppliers, receiving, checkout, returns, every report definition, **identity, audit, backup and restore** | 77 |

Integration tests need a reachable SQL Server; they create `MediStock_Test`, migrate it, and drop it
on teardown. Point them elsewhere with `MEDISTOCK_TEST_CONNECTION`.

> The count is the last **verified** figure (Milestone 4, all green). Milestone 5 added tests that
> have not yet been counted here — see §8.

---

## 8. What is not finished

### Milestone 5 — in progress

| Item | State |
|---|---|
| Users & Settings, Accounts tab | ✅ working, exercised by hand |
| Users & Settings, Audit trail tab | ✅ working |
| Users & Settings, Backup tab | ✅ working — a backup was taken, listed and sized |
| Users & Settings, Support tab | ✅ working |
| Backup **restore** end to end from the UI | ⚠️ implemented, not yet exercised against a live database |
| Refusal paths for a lesser role (pharmacist sees refusal notes, not empty panels) | ⚠️ implemented, not yet exercised |
| First-run setup on a genuinely empty database | ⚠️ exercised earlier in the milestone; not re-verified since the last schema change |
| New tests for identity, audit and backup | ⏳ written? no — still to do |
| ADR 0009 and 0010 | ⏳ not written |
| `database-design.md` §3.14–3.15 and the new invariants | ⏳ not written |
| Full-suite run after the Milestone 5 UI changes | ⏳ owed |

### Known rough edges

- The audit summary deliberately distinguishes an unknown account name from a wrong password, while
  the interactive refusal message is identical for both. This is a decision, not an oversight, and
  needs to be written down in ADR 0009.
- `InstantTextConverter` uses the **machine** time zone so screen and printed receipts agree, while
  `IClock` uses the configured `TimeZoneId`. On a one-PC installation these are the same thing; on a
  machine configured otherwise they will drift. Documented in the converter; worth revisiting.
- The audit list shows six rows at a time; the data grid virtualises, so its row count reflects what
  is on screen rather than how many rows the query returned.
- There are no `AutomationId`s anywhere in the Desktop project, which makes UI Automation smoke tests
  fragile — elements have to be found by name and control type.

### Not implemented at all

Manual stock adjustments · expiry write-offs · supplier returns · quarantined-stock view · PDF and
Excel export · barcode label printing · purchase orders and approvals · installer and MSIX packaging
· automatic upgrades · multi-user over a LAN · multi-branch · any import tooling.

---

## 9. How to run it

```powershell
Set-Location C:\Users\Imran\source\repos\MediStock

sqllocaldb start MSSQLLocalDB     # or .\scripts\dev\start-localdb.ps1
dotnet build  MediStock.slnx
dotnet test   MediStock.slnx
dotnet run --project src\MediStock.Desktop
```

Sign in as `admin` / `Kestrel-Harbour-71`, or use `scripts\dev\reset-database.ps1` to rebuild the
development database from migrations and seed it again.

`scripts\dev\check-reconciliation.ps1` runs six independent checks and prints the row counts,
inventory value and takings; a healthy database reports `OK` for all six.

---

## 10. Status of the working tree

At the time of writing, Milestone 5 is **uncommitted on top of `7e5dddf`**: 21 modified files and 14
new paths, including the whole of `src\MediStock.Domain\Identity`,
`src\MediStock.Application\Identity`, `src\MediStock.Infrastructure\Identity`, the sign-in window,
the Users & Settings screen and the new theme styles.

The tree builds with **0 warnings and 0 errors**. The last full test run was at the end of
Milestone 4 (286 green); the Milestone 5 changes have not yet had a full run — that is the next
thing to do, and it is item `m5-verify` in [`plan.md`](plan.md) §4.
