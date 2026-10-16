# MediStock

MediStock is a Windows desktop application for pharmacy management: a medicine catalogue,
batch-level inventory, suppliers, purchasing, point-of-sale billing, returns, expiry tracking and
operational reporting.

The project is being built milestone by milestone against the plan in
[docs/roadmap.md](docs/roadmap.md). The guiding principle is **correctness and traceability first,
convenience second, advanced features third**.

---

## Current status

| Milestone | Scope | State |
|---|---|---|
| **0** | Environment, solution, DI, logging, application shell, database connectivity | ✅ Complete |
| **1** | Database schema, medicine catalogue, dashboard read models | ✅ Complete |
| **2** | Suppliers, purchasing and receiving, batch creation, stock movements UI | ✅ Complete |
| **3** | Point of sale: cart, FEFO allocation, pricing and tax, transactional checkout, invoices and receipts | ✅ Complete |
| **4** | Sales returns and dispositions, cancellations, low-stock and expiry views, ten operational reports with CSV export | ✅ Complete |
| 4b | Manual stock adjustments, expiry write-offs, supplier returns, quarantined-stock visibility | ⏳ Planned |
| 5 | Users, permissions, audit trail, backup/restore | ⏳ Not started |
| 6 | Packaging and release | ⏳ Not started |

What works today:

- The WPF shell launches with sidebar navigation, a top bar and a live connection indicator.
- EF Core migrations are applied on startup and the development database is seeded with fictional data.
- The dashboard shows today's sales, low stock, near-expiry batches and recent stock movements.
- The medicine catalogue supports search (name, brand, generic name, barcode), create, edit and
  deactivate, including duplicate warnings and validation.
- Suppliers can be created, edited, searched and deactivated, with a warning when an invoice number
  has already been received from that supplier.
- Purchases can be created as drafts, edited, received into stock, reversed or cancelled. Receiving
  creates one batch and one stock movement per line inside a single transaction, and every lifecycle
  transition is a conditional write: two operators acting on the same purchase at the same moment
  cannot both take the stock in — the loser is told to reload.
- Inventory & batches lists stock with sellable/non-sellable states, expiry filters and a full stock
  movement history, and reports whether every cached balance still equals its ledger total.
- The counter (**New Sale**) searches or scans medicines into a cart, shows eligible batches with a
  preferred-batch override, applies the catalogue tax rate, takes one or more payments, and refuses a
  sale outright when stock is short or expired. Allocating, pricing, numbering the invoice, reducing
  batches, writing the movements and taking the money happen in one transaction, so a failed sale
  leaves nothing behind. Sales history searches invoices by number, customer or medicine, reprints a
  receipt, and shows the day's takings split by payment method. An uncompleted invoice can be cancelled,
  which reverses its stock in the same transaction and keeps the invoice number on record.
- **Returns & adjustments** takes back part or all of an invoice: it looks up the original sale by number,
  caps every entry at the quantity still returnable, shows what the return would refund before anything is
  written, and requires a reason plus a per-line disposition (restock, quarantine or destroy). Returned
  stock only goes back on the shelf when the operator says so *and* the batch is still within its expiry
  policy; quarantined and destroyed lines write no movement at all. Refunds and returned cost are apportioned
  across the original lines to the paisa, and restocking returns stock to the batch it was sold from.
- **Reports** renders ten operational reports — daily sales, sales by medicine, sales by payment method,
  purchase history, stock by batch, low stock, expiring stock, stock movements, inventory valuation and
  sales returns — as filterable tables, and exports any of them to RFC 4180 CSV with a formula-injection guard.

Expiry write-offs, manual stock adjustments, supplier returns and PDF/Excel export are **not** implemented yet.

---

## Prerequisites

| Requirement | Notes |
|---|---|
| Windows 10/11 or Windows Server 2019+ | WPF is Windows-only |
| [.NET SDK 10](https://dotnet.microsoft.com/download) | Built and tested with **10.0.402** |
| Visual Studio 2026 with the **.NET desktop development** workload | Optional if you prefer the CLI |
| SQL Server 2025 Express **LocalDB** | Zero-configuration development database |
| `dotnet-ef` global tool | `dotnet tool install --global dotnet-ef` (10.0.13) |

Verify the toolchain:

```powershell
dotnet --version
dotnet tool list --global
sqllocaldb info MSSQLLocalDB
```

### SQL Server instance

Development defaults to LocalDB. `sqllocaldb` is a management helper; the database engine is what
actually runs your queries, so the instance must be started before the application connects:

```powershell
sqllocaldb start MSSQLLocalDB
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "SELECT @@VERSION"
```

To use a full SQL Server instance instead, either edit `ConnectionStrings:MediStock` in
`src/MediStock.Desktop/appsettings.json` or — preferred, because nothing machine-specific gets
committed — set the override environment variable:

```powershell
$env:MEDISTOCK_CONNECTION = "Server=.\SQLEXPRESS;Database=MediStock;Trusted_Connection=True;TrustServerCertificate=True"
```

The override is checked first and wins over configuration.

> **Note:** SQL Server Developer / Express editions are for development and testing only. Choose an
> appropriate edition and licensing model before deploying to a pharmacy, and grant the application
> least-privilege database access. Never commit credentials.

---

## Build, run and test

```powershell
Set-Location C:\Users\Imran\source\repos\MediStock

dotnet build MediStock.slnx                       # compile everything
dotnet test  MediStock.slnx                       # run all 286 tests
dotnet run   --project src\MediStock.Desktop      # launch the application
```

> The solution file is `MediStock.slnx` (the .NET 10 XML solution format), not `MediStock.sln`.
> Every `dotnet` command must reference that exact name.

Publish a self-contained build:

```powershell
dotnet publish src\MediStock.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Tests

| Project | What it covers | Tests |
|---|---|---|
| `MediStock.Domain.Tests` | Money rounding and proportional shares, guards, medicine rules, expiry policy, FEFO ordering, batch balances and restock eligibility, movements, purchase lifecycle, sale pricing and settlement, returns and refund estimation | 191 |
| `MediStock.Application.Tests` | DTO/draft normalisation, `OperationResult` semantics | 18 |
| `MediStock.IntegrationTests` | Migrations, schema constraints, catalogue, supplier, receiving, checkout and return workflows, and every report definition, including two tills racing for the same last units, six racing for invoice numbers and two racing to return the same units, against real SQL Server | 77 |

Integration tests need a reachable SQL Server. They create **`MediStock_Test`** in
`(localdb)\MSSQLLocalDB`, migrate it, and drop it again on teardown — never production or personal
data. Point them elsewhere with `MEDISTOCK_TEST_CONNECTION`.

---

## Configuration

Configuration is layered; later sources win.

1. `src/MediStock.Desktop/appsettings.json` — committed defaults, **no secrets**.
2. `src/MediStock.Desktop/appsettings.Development.json` — committed, applied in `DEBUG` builds; enables seeding.
3. `src/MediStock.Desktop/appsettings.Local.json` — **git-ignored**; machine-specific overrides.
   Copy `appsettings.Local.example.json` to get started.
4. `MEDISTOCK_CONNECTION` — environment variable, highest priority for the connection string.

Settings under the `MediStock` section:

| Key | Default | Meaning |
|---|---|---|
| `PharmacyName` | `MediStock Pharmacy` | Printed on invoices and the shell header |
| `TimeZoneId` | `India Standard Time` | Zone used to derive the local business date for reports |
| `NearExpiryDays` | `90` | Window for the near-expiry list |
| `ApplyMigrationsOnStartup` | `true` | Apply pending EF Core migrations at launch |
| `SeedDevelopmentData` | `false` (true in Development) | Insert fictional sample data when the catalogue is empty |
| `ReceiptFolder` | `MediStock\Receipts` | Where exported receipts are written. A relative path is resolved against the user's documents folder |
| `ReportFolder` | `MediStock\Reports` | Where exported report files are written, resolved the same way |

Application logs are written to `%LOCALAPPDATA%\MediStock\logs\medistock-yyyyMMdd.log`.

### Database management

```powershell
dotnet ef migrations add <Name> --project src\MediStock.Infrastructure --startup-project src\MediStock.Desktop
dotnet ef database update      --project src\MediStock.Infrastructure --startup-project src\MediStock.Desktop
dotnet ef database drop --force --project src\MediStock.Infrastructure --startup-project src\MediStock.Desktop
```

Schema changes are applied **only** through versioned migrations — never by hand-editing a database.

### Helper scripts

| Script | Purpose |
|---|---|
| `scripts\dev\start-localdb.ps1` | Starts `MSSQLLocalDB` and proves it answers a query. Run this first on a fresh machine. |
| `scripts\dev\reset-database.ps1` | Drops and rebuilds the development database from migrations alone. Use after a failed startup leaves partial data behind. |
| `scripts\dev\check-reconciliation.ps1` | Runs six independent checks: every cached batch balance equals its ledger sum; each invoice's batch allocations match its sale movements; each invoice's lines add up to its stored total; invoice lines add up to what their returns have recorded; each return header matches its own lines; and every restocked return has the stock movement to prove it. Then prints row counts, the inventory value at cost and the takings on record. |

```powershell
.\scripts\dev\start-localdb.ps1
.\scripts\dev\reset-database.ps1
.\scripts\dev\check-reconciliation.ps1
```

A healthy seeded development database reports **10 medicines, 4 suppliers, 8 purchases, 13 purchase
lines, 11 batches, 19 stock movements, 3 invoices, 6 sale lines, 6 allocations, 4 payments and 1 sales
return of 2 lines**, an inventory value at cost of **₹7,378.20**, takings of **₹489.02** on record, and
`OK` for all six reconciliation checks.

### Development seed data

Development startup inserts fictional data only when the medicine catalogue is empty: 10 medicines,
4 suppliers, 8 purchases, 11 batches and 19 stock movements in all — 11 receipts, one reversed receipt,
six sale movements, and one from the sample return. Three of those purchases are then rung up as real
invoices through the real checkout workflow and one of the invoices is partly returned through the real
returns workflow and restocked, so the seeded database already has sales, payments, batch allocations
and the `RET-2026-000001` return, with the movements all of them produced. The sample sizes are chosen
to exercise low-stock, near-expiry, multiple-batch-per-medicine and part-return scenarios. Seed data is
fictional and clearly separated from real pharmacy data; keep it that way.

---

## Repository layout

```plaintext
MediStock/
├── MediStock.slnx
├── Directory.Build.props       # shared build settings for every project
├── src/
│   ├── MediStock.Desktop/          # WPF views, view models, navigation, UI resources
│   ├── MediStock.Application/      # use cases, DTOs, validation, service interfaces
│   ├── MediStock.Domain/           # entities, value objects, business rules
│   └── MediStock.Infrastructure/   # EF Core, SQL Server, repositories, logging adapters
├── tests/
│   ├── MediStock.Domain.Tests/
│   ├── MediStock.Application.Tests/
│   └── MediStock.IntegrationTests/
├── docs/
│   ├── architecture.md
│   ├── database-design.md
│   ├── roadmap.md
│   └── decisions/                  # architecture decision records
└── scripts/dev/
```

Dependencies point inwards: `Desktop → Application`, `Infrastructure → Application`, and `Domain`
depends on nothing. Neither the domain nor the application layer references WPF or EF Core.

---

## Documentation

- [Architecture](docs/architecture.md) — layers, composition root, threading and error handling.
- [Database design](docs/database-design.md) — tables, constraints, indexes and the invariants they enforce.
- [Roadmap](docs/roadmap.md) — milestones, tasks and acceptance criteria.
- [Decision records](docs/decisions/) — why the project looks the way it does.

---

## Conventions

- `decimal` for money, never floating point; explicit midpoint rounding.
- Quantity is `decimal(18,3)`; money is `decimal(18,2)`.
- Timestamps are `DateTimeOffset`; business dates are `DateOnly` derived from the configured zone.
- Every stock change writes an append-only `StockMovements` row. The ledger is the source of truth.
- No hard deletes of transactional history; records are deactivated or reversed instead.
- Keep business rules out of views; they belong in testable domain and application code.
- Use `CancellationToken` for long-running work and keep transactions short.
- Commit small, working increments.

---

## Important disclaimer

MediStock is a learning project and is **not** certified, licensed or validated for clinical,
regulatory or legal use. Before it is used with real prescriptions, stock or money, a qualified
pharmacist must validate the medicine, prescription, returns, expiry and record-retention
workflows, and the applicable licensing, drug-sale, tax-invoice, controlled-medicine and
data-protection obligations must be confirmed with qualified professionals. Nothing here should be
presented as legally compliant until such a review has taken place.
