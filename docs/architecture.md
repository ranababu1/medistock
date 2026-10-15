# MediStock architecture

## 1. Style

A conventional layered desktop application: a WPF/MVVM presentation layer over an application layer
of use cases, a dependency-free domain model, and an infrastructure layer that owns EF Core and
SQL Server. Dependencies point inwards only.

```plaintext
        ┌──────────────────────────────────────────────┐
        │  MediStock.Desktop  (WPF, XAML, MVVM)         │
        │  views · view models · navigation · logging  │
        └───────────────────────┬──────────────────────┘
                                │ depends on
        ┌───────────────────────▼──────────────────────┐
        │  MediStock.Application                       │
        │  use cases · DTOs · validation · interfaces  │
        └───────────────────────┬──────────────────────┘
                                │ depends on
        ┌───────────────────────▼──────────────────────┐
        │  MediStock.Domain                            │
        │  entities · value objects · business rules   │
        └──────────────────────────────────────────────┘
                                ▲
                                │ implements application interfaces
        ┌───────────────────────┴──────────────────────┐
        │  MediStock.Infrastructure                    │
        │  DbContext · EF configurations · migrations  │
        │  query services · seeding · health checks    │
        └──────────────────────────────────────────────┘
```

| Project | Target | References | Responsibility |
|---|---|---|---|
| `MediStock.Domain` | `net10.0` | — | Entities, value objects and business rules. No EF Core, no WPF, no I/O. |
| `MediStock.Application` | `net10.0` | Domain, `Microsoft.Extensions.Logging.Abstractions` | Use cases, DTOs, validation, the interfaces infrastructure implements. |
| `MediStock.Infrastructure` | `net10.0` | Application, EF Core + SQL Server provider | `DbContext`, EF configurations, migrations, query services, seeding, DB health checks. |
| `MediStock.Desktop` | `net10.0-windows` | Application, Infrastructure, `Microsoft.Extensions.Hosting`, `CommunityToolkit.Mvvm` | WPF shell, views, view models, DI composition root, file logging. |

The desktop project references Infrastructure **only** so the composition root can call
`AddMediStockInfrastructure`. View models resolve application interfaces; they never touch
`DbContext` or EF Core types.

## 2. Why these boundaries

- **Domain has zero dependencies.** Business rules can be tested in milliseconds and cannot be
  bypassed by an accidental UI shortcut.
- **Application defines the contracts.** `IMedicineCatalog`, `IDashboardQueries`,
  `IDatabaseHealthCheck` and `IClock` live next to the code that uses them, so Infrastructure is
  free to change without touching the UI.
- **No generic repositories.** Aggregates are loaded through purpose-shaped query services that
  return DTOs. A generic `IRepository<T>` would add indirection without solving a problem this
  application actually has.
- **Composition root only in Desktop.** `App.xaml.cs` is the single place that knows about both
  layers, which keeps the wiring visible and greppable.

## 3. Layers in detail

### 3.1 Domain — `MediStock.Domain`

| Area | Types |
|---|---|
| Common | `DomainException`, `DomainValidationException`, `Guard`, `Money` |
| Catalogue | `Medicine`, `DosageForm` |
| Suppliers | `Supplier` |
| Inventory | `MedicineBatch`, `BatchStatus`, `StockMovement`, `StockMovementType`, `InsufficientStockException`, `BatchAvailability`, `BatchAllocation`, `FefoAllocator`, `ExpiryPolicy` |

Rules enforced here, not in the UI:

- A medicine must have a display name; duplicates are *warned* about, not blocked.
- An expiry date must fall after the received date, and `BatchStatus` transitions are explicit.
- Quantity on hand can never go negative and never exceeds the quantity received.
- A stock movement's `QuantityDelta` is never zero; `QuantityAfter` is never negative.
- Certain movement types (`Adjustment`, `Expiry write-off`, `Damage`, `Return`, …) require a reason.
- `FefoAllocator` allocates stock first-expiry-first-out across eligible batches only.

`Money` centralises currency rounding so no call site picks its own `MidpointRounding` — see
[ADR 0003](decisions/0003-inventory-valuation.md).

### 3.2 Application — `MediStock.Application`

| Area | Types |
|---|---|
| Common | `OperationResult`, `OperationResult<T>`, `IClock` |
| Configuration | `MediStockOptions` |
| Diagnostics | `IDatabaseHealthCheck`, `DatabaseHealthReport` |
| Catalogue | `MedicineDto`, `MedicineDraft`, `MedicineSearchQuery`, `IMedicineCatalog` |
| Dashboard | `IDashboardQueries`, `DashboardSnapshot`, `LowStockItem`, `ExpiringBatch` |
| Sales | `SaleDraft`, `SaleLineDraft`, `PaymentDraft`, `SaleQuote`, `SaleDto`, `SaleLineDto`, `SaleAllocationDto`, `PaymentDto`, `SaleQuery`, `ISaleCatalog`, `ISalesQueries`, `IReceiptExporter` |

`OperationResult` (and its generic form) is the standard outcome for a use case: a success flag, a
message and a list of errors. `OperationResult.From(Exception)` maps `DomainException` messages
through verbatim because they are written for users, and collapses everything else to a generic
message pointing at the log, so a raw SQL or connection-string failure can never surface in the UI.

Validation is performed on *drafts* (`MedicineDraft`) before a domain entity is built, so the UI gets
a complete list of problems in one pass instead of one exception at a time.

### 3.3 Infrastructure — `MediStock.Infrastructure`

| Area | Types |
|---|---|
| Persistence | `MediStockDbContext`, `Configurations/*`, `MediStockDbContextFactory` |
| Persistence/Seeding | `DevelopmentDataSeeder` |
| Catalogue | `EfMedicineCatalog` |
| Suppliers | `EfSupplierCatalog` |
| Purchasing | `EfPurchaseCatalog` |
| Inventory | `EfStockQueries` |
| Sales | `EfSaleCatalog`, `EfSalesQueries`, `EfInvoiceNumberAllocator`, `TextReceiptExporter` |
| Dashboard | `EfDashboardQueries` |
| Diagnostics | `SqlServerDatabaseHealthCheck` |
| Time | `SystemClock` |
| Composition | `DependencyInjection` |

`MediStockDbContextFactory` implements `IDesignTimeDbContextFactory<MediStockDbContext>` so
`dotnet ef` works without booting WPF.

`DependencyInjection` is the one place that knows the connection string. Resolution order:
`MEDISTOCK_CONNECTION` environment variable → `ConnectionStrings:MediStock` configuration.
`LogDatabaseTarget` logs server/database names with no credentials.

### 3.4 Desktop — `MediStock.Desktop`

```plaintext
Desktop/
├── App.xaml, App.xaml.cs              composition root: host, DI, migration + seed on startup
├── appsettings*.json                  layered configuration
├── Themes/Theme.xaml                  colours, brushes, control styles
├── Converters/CommonConverters.cs     value converters for XAML bindings
├── Logging/FileLoggerProvider.cs      rolling daily file log
├── Services/                          INavigationService, IDialogService, NavigationRoutes
├── ViewModels/                        ShellViewModel, DashboardViewModel, MedicinesViewModel,
│                                      MedicineEditorViewModel, SuppliersViewModel,
│                                      SupplierEditorViewModel, PurchasesViewModel,
│                                      PurchaseEditorViewModel, PurchaseLineEditorViewModel,
│                                      InventoryViewModel, NewSaleViewModel, CartLineViewModel,
│                                      SalesHistoryViewModel, PlaceholderViewModel
└── Views/                             ShellWindow, DashboardView, MedicinesView, SuppliersView,
                                       PurchasesView, InventoryView, NewSaleView,
                                       SalesHistoryView, TextPromptWindow, PlaceholderView
```

- **Composition root.** `App.OnStartup` builds a `HostApplicationBuilder`, calls
  `AddMediStockInfrastructure`, registers the navigation/dialog services and the window and view
  models, resolves `ShellWindow` and shows it. Startup failures are caught, logged and reported in a
  dialog rather than crashing silently.
- **Database preparation.** `PrepareDatabaseAsync` applies pending migrations when
  `MediStock:ApplyMigrationsOnStartup` is set and runs the development seeder when
  `MediStock:SeedDevelopmentData` is set *and* the catalogue is empty.
- **MVVM.** View models derive from `ViewModelBase`, use the CommunityToolkit.Mvvm source
  generators (`[ObservableProperty]`, `[RelayCommand]`) and hold no reference to WPF controls.
  Views bind to them; `ShellWindow.xaml` hosts the navigation rail, the top bar and the content
  region.
- **Navigation.** `INavigationService` maps a `NavigationRoutes` key to a view model and swaps the
  content region. Placeholders advertise the milestones they belong to instead of dead links.
- **Logging.** `FileLoggerProvider` writes to `%LOCALAPPDATA%\MediStock\logs\medistock-yyyyMMdd.log`
  in addition to the console, so a support engineer can read what happened on a user's PC. Logs
  never contain credentials or unnecessary personal data.

## 4. Cross-cutting behaviour

**Threading.** Database work is asynchronous and awaited on the UI thread; view models never block
with `.Result` or `.Wait()`. Long operations accept a `CancellationToken` sourced from the command.

**Time.** `IClock` supplies `UtcNow` (`DateTimeOffset`) and `BusinessDate` (`DateOnly`) derived from
`MediStock:TimeZoneId`. Reports and "today" figures use the business date, so a shift spanning
midnight is attributed to the pharmacy's local day rather than UTC. `SystemClock` is the only
implementation, and tests substitute a fixed clock where needed.

**Errors.** Expected problems travel as `OperationResult` and render inline. Unexpected exceptions
are logged with full detail and reported generically. Constraint violations from SQL Server are
translated at the infrastructure boundary (SQL error 547 = constraint, 2601 = duplicate key).

**Concurrency.** The database is the arbiter. Check constraints, foreign keys, transactions and
concurrency tokens enforce the invariants; a UI check is a courtesy, never the guarantee.

**Conditional lifecycle writes.** A row that carries a mutable lifecycle maps a `rowversion` shadow
property as an EF Core concurrency token: `Purchases` does (ADR 0006) and so does `MedicineBatches`,
whose quantity is decremented by every sale. EF Core then emits
`UPDATE … WHERE [Id] = @id AND [RowVersion] = @original` and raises
`DbUpdateConcurrencyException` when another operation committed first. Because the exception is raised
inside the unit of work and the code returns without committing, the whole transaction — including any
batch inserts or quantity decrements — rolls back. The catalog converts it into a message the operator
can act on ("reload the purchase", or "check the basket against current stock"), never a stack trace.
A write path that mutates a balance without such a token is a bug: an unconditional
`UPDATE … WHERE [Id] = @id` silently overwrites newer state. A row that is only ever inserted — `Sales`,
`SaleLines`, `Payments` — needs none, and gets none; Milestone 4 must add one to `Sales` before it
writes the first status change.

**Transactions.** A sale, its lines, payments, allocations and stock movements commit together or not
at all. The checkout writes in one order inside one transaction — price the basket, stop if blocked,
record the payments, take the invoice number, build the sale, decrease the batches, save, write one
movement per batch, save, commit — and reduces stock last, so nothing can leave the shelf behind an
invoice that was never written. The rule for every future write path is: do not reduce stock before the
sale is safely committed. See [ADR 0007](decisions/0007-point-of-sale-pricing-and-settlement.md).

**Context lifetime.** A desktop application has no request boundary, so a scoped `DbContext` would be
shared by the whole process and two overlapping `await`s would collide on it. Infrastructure therefore
registers an `IDbContextFactory<MediStockDbContext>`, and every catalog and query class opens one
short-lived context per public method. A caller that needs a single context across several steps —
migrations, the seeder, the integration test fixture — resolves `MediStockDbContext` from its own
scope and owns its disposal.

**Retries and transactions.** The connection enables `EnableRetryOnFailure`, and EF Core refuses to
run a statement inside a user-initiated transaction unless the whole unit of work is handed to its
execution strategy. Every write path that opens a transaction therefore runs it through
`Database.CreateExecutionStrategy().ExecuteAsync(...)`, clears the change tracker at the start of each
attempt, and performs reloading and logging only after the strategy. See
[ADR 0006](decisions/0006-context-lifetime-and-execution-strategy.md).

The execution strategy retries transient SQL faults; it does **not** retry a concurrency conflict,
because that is EF Core's own exception rather than one SQL Server raised. The checkout therefore wraps
the strategy in its own bounded attempt loop: a basket that lost a race for the stock it was priced
against is read, re-priced and written again (up to eight attempts, with a short growing pause) before
the operator is told the shelf moved under them. A refusal therefore means the stock really is short —
not merely that another till was busy.

Two details keep that loop honest. The transaction is opened *inside* the unit of work, so a failed
attempt is rolled back by disposing it and the next attempt starts clean — the retry never rejoins a
half-written transaction. And because the connection enables MARS, EF Core disables savepoints and logs
`Savepoints are disabled because Multiple Active Result Sets (MARS) is enabled` whenever it opens one;
that warning is expected. It is precisely the reason the unit of work is retried as a whole rather than
resumed: after a failed attempt the application, not EF Core, is responsible for rolling back, and
disposing the transaction does it.

**Loading an invoice.** A sale aggregate carries two collections — lines (with their batch allocations)
and payments — so it is loaded with `AsSplitQuery()`: one round trip per collection instead of one
round trip returning lines × payments rows for every invoice in a search result.

## 5. Testing

| Level | Project | Approach |
|---|---|---|
| Unit | `MediStock.Domain.Tests` | Pure rules: money, guards, catalogue validation, expiry policy, FEFO ordering, batch balances, movement invariants, purchase lifecycle, sale-line pricing and settlement. No database. |
| Unit | `MediStock.Application.Tests` | DTO/draft normalisation and `OperationResult` semantics. |
| Integration | `MediStock.IntegrationTests` | Real SQL Server: migrations, every named check constraint/index, catalogue CRUD and search, the receiving workflow end to end, and the counter — allocation, pricing, transactional checkout, racing tills and invoice numbering. |

`TestDatabaseFixture` creates `MediStock_Test` (override with `MEDISTOCK_TEST_CONNECTION`), applies
migrations, exposes a scope per test, and drops the database on teardown. It never touches a
development or production database. Integration tests share one fixture through the
`"SqlServer"` xUnit collection so the database is created once per run.

## 6. Known limitations

- LocalDB is the development target; a production deployment needs a deliberate choice of SQL
  Server edition and licensing.
- Single-operator mode only — authentication, roles and the audit trail arrive in Milestone 5. The
  `SoldBy` column on `Sales` and `Purchases` records a typed name, not a verified identity.
- The counter prices a line at the selling price of the first batch allocated and applies one tax rate
  per line. Inclusive pricing, percentage or basket-level discounts, per-allocation pricing, HSN codes
  and statutory rounding are unmodelled until a pharmacist and a tax professional confirm the real
  rules — see [ADR 0007](decisions/0007-point-of-sale-pricing-and-settlement.md).
- Returns, cancellations, expiry write-offs and supplier returns arrive in Milestone 4. A completed
  invoice currently has no path back: the money and the stock stay where they are.
- Report export is a text receipt per invoice; CSV and PDF reports are Milestone 4 work.
