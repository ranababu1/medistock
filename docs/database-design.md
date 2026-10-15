# MediStock database design

The schema currently implements the catalogue, supplier and inventory foundations (Milestones 0–1).
Purchase, sales, returns, identity and audit tables are described in
[roadmap.md](roadmap.md) and will be added by later migrations.

Everything in this document is the **current** state of `20261015214039_InitialCreate`. Schema
changes happen only through new EF Core migrations.

---

## 1. Principles

1. **Money is `decimal`, never floating point.** Amounts are `decimal(18,2)`; quantities are
   `decimal(18,3)`.
2. **The stock ledger is the source of truth.** `StockMovements` is append-only; a cached balance is
   only a performance shortcut and must always reconcile with the ledger.
3. **No hard deletes of transactional history.** Rows are deactivated (`IsActive = 0`) or moved to a
   terminal `BatchStatus` instead.
4. **The database is the arbiter.** Check constraints, foreign keys and transactions enforce the
   invariants; a UI check is a courtesy, never the guarantee.
5. **Parameterised SQL only.** No string-concatenated queries, ever.
6. **Time is unambiguous.** Instants are `datetimeoffset(3)`; report dates are `date`.
7. **Names are stable.** Constraint and index names are referenced by integration tests, so renaming
   one is a deliberate, reviewable change.

## 2. Type conventions

| Concept | SQL type | .NET type | Notes |
|---|---|---|---|
| Surrogate key | `int IDENTITY` | `int` | Clustered primary key on every table |
| Money | `decimal(18,2)` | `decimal` | `MidpointRounding.AwayFromZero` via `Money` |
| Quantity | `decimal(18,3)` | `decimal` | Supports split packs and fractions of a unit |
| Instant | `datetimeoffset(3)` | `DateTimeOffset` | Always UTC, `...Utc` suffix in the property name |
| Business date | `date` | `DateOnly` | Local pharmacy day from `MediStock:TimeZoneId` |
| Name / label | `nvarchar(n)` | `string` | Max lengths enforced by EF configuration |
| Enum | `int` | `enum` | Guarded by a check constraint |
| Flag | `bit` | `bool` | Non-nullable with a default |

`DateTimeOffset` is stored at millisecond precision. Two writes inside the same millisecond are
therefore indistinguishable — do not use timestamp ordering as a tie-breaker.

## 3. Tables

### 3.1 `Medicines`

Medicine-level catalogue information, kept deliberately separate from stock batches.

| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | `int IDENTITY` | no | PK |
| `DisplayName` | `nvarchar(200)` | no | The name users search and see |
| `BrandName` | `nvarchar(200)` | yes | e.g. `FeverKare` |
| `GenericName` | `nvarchar(200)` | yes | e.g. `Paracetamol` |
| `Strength` | `nvarchar(200)` | yes | e.g. `500 mg` |
| `DosageForm` | `int` | no | `0`=Unknown, `1`=Tablet, `2`=Capsule, `3`=Syrup, `4`=Suspension, `5`=Injection, `6`=Ointment, `7`=Cream, `8`=Drops, `9`=Inhaler, `10`=Powder, `11`=Sachet, `12`=Suppository, `13`=Patch, `14`=Gel, `15`=Solution, `16`=Spray, `99`=Other. Not constrained: `Unknown` is a legal value for an incomplete catalogue entry |
| `Manufacturer` | `nvarchar(200)` | yes | Promoted to its own table later if needed |
| `Barcode` | `nvarchar(64)` | yes | Scannable product code |
| `PackDescription` | `nvarchar(100)` | yes | Free-text pack description only |
| `ReorderThreshold` | `decimal(18,3)` | yes | Below this, the medicine appears in low-stock lists |
| `IsActive` | `bit` | no | Inactive medicines stay for history but cannot be sold |
| `CreatedAtUtc` | `datetimeoffset(3)` | no | |
| `UpdatedAtUtc` | `datetimeoffset(3)` | no | |

**Indexes and constraints**

- `UX_Medicines_Barcode` — unique, filtered `WHERE [Barcode] IS NOT NULL`. Two medicines may both
  have *no* barcode, but not the same barcode.
- `IX_Medicines_DisplayName`, `IX_Medicines_GenericName` — search support.
- `CK_Medicines_ReorderThreshold_NonNegative`.

### 3.2 `Suppliers`

| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | `int IDENTITY` | no | PK |
| `Name` | `nvarchar(200)` | no | |
| `ContactPerson` | `nvarchar(150)` | yes | |
| `Phone` | `nvarchar(32)` | yes | |
| `Email` | `nvarchar(256)` | yes | |
| `AddressLine` | `nvarchar(400)` | yes | |
| `City` | `nvarchar(100)` | yes | |
| `State` | `nvarchar(100)` | yes | |
| `PostalCode` | `nvarchar(16)` | yes | |
| `Gstin` | `nvarchar(15)` | yes | Indian GST identification number |
| `DrugLicenseNumber` | `nvarchar(40)` | yes | |
| `Notes` | `nvarchar(1000)` | yes | |
| `IsActive` | `bit` | no | Retained for historical transactions when inactive |
| `CreatedAtUtc` | `datetimeoffset(3)` | no | |
| `UpdatedAtUtc` | `datetimeoffset(3)` | no | |

**Indexes and constraints**

- `UX_Suppliers_Name` — unique. Supplier names are the natural key for humans.
- `UX_Suppliers_Gstin` — unique, filtered `WHERE [Gstin] IS NOT NULL`.

### 3.3 `MedicineBatches`

One row per physical batch received. This is the only place a sellable quantity and a price live.

| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | `int IDENTITY` | no | PK |
| `MedicineId` | `int` | no | FK → `Medicines.Id` |
| `BatchNumber` | `nvarchar(64)` | no | Manufacturer/supplier batch code |
| `ExpiryDate` | `date` | no | **Last day the batch may be dispensed** — see [ADR 0002](decisions/0002-expiry-date-interpretation.md) |
| `QuantityReceived` | `decimal(18,3)` | no | Immutable original quantity |
| `QuantityOnHand` | `decimal(18,3)` | no | Cached balance; must equal the ledger sum |
| `PurchaseCost` | `decimal(18,2)` | no | Per unit |
| `SellingPrice` | `decimal(18,2)` | no | Per unit |
| `SupplierId` | `int` | yes | FK → `Suppliers.Id` |
| `SupplierInvoiceNumber` | `nvarchar(64)` | yes | |
| `ReceivedOn` | `date` | no | Business date of receipt |
| `ReceivedAtUtc` | `datetimeoffset(3)` | no | |
| `UpdatedAtUtc` | `datetimeoffset(3)` | no | |
| `Status` | `int` | no | `1`=Active, `2`=Depleted, `3`=Expired, `4`=WrittenOff, `5`=Recalled |
| `StatusReason` | `nvarchar(300)` | yes | Required for write-off and recall; set automatically for expiry |

**Indexes and constraints**

| Name | Definition |
|---|---|
| `CK_MedicineBatches_QuantityReceived_Positive` | `QuantityReceived > 0` |
| `CK_MedicineBatches_QuantityOnHand_NonNegative` | `QuantityOnHand >= 0 AND QuantityOnHand <= QuantityReceived` |
| `CK_MedicineBatches_Prices_NonNegative` | `PurchaseCost >= 0 AND SellingPrice >= 0` |
| `CK_MedicineBatches_ExpiryAfterReceipt` | `ExpiryDate > ReceivedOn` |
| `CK_MedicineBatches_Status_Valid` | `Status BETWEEN 1 AND 5` |
| `IX_MedicineBatches_MedicineId_ExpiryDate` | FEFO allocation; also drives the near-expiry list |
| `IX_MedicineBatches_ExpiryDate` | Expiry sweeps across all medicines |
| `IX_MedicineBatches_BatchNumber` | Batch lookup |
| `IX_MedicineBatches_SupplierId_Invoice` | Invoice lookup and duplicate-invoice warnings |

Foreign keys to `Medicines` and `Suppliers` use `DeleteBehavior.Restrict`, so a medicine or supplier
with history cannot be removed.

> **Deliberately not unique:** `(MedicineId, BatchNumber, ExpiryDate)`. The same batch number can
> legitimately arrive twice (return from a customer, partial reorder, supplier relabelling).
> Duplicate-invoice detection is a *warning*, not a constraint — see
> [ADR 0001](decisions/0001-record-architecture-decisions.md) for why the plan's "idempotency or
> clear warning" requirement is satisfied in the application layer instead.

### 3.4 `StockMovements`

Append-only ledger. Every receipt, sale, return, adjustment and write-off writes exactly one row.

| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | `int IDENTITY` | no | PK |
| `MedicineBatchId` | `int` | no | FK → `MedicineBatches.Id` (`Restrict`) |
| `MedicineId` | `int` | no | Denormalised for reporting; no FK, see below |
| `MovementType` | `int` | no | `1`=Receipt, `2`=Sale, `3`=SalesReturn, `4`=AdjustmentIncrease, `5`=AdjustmentDecrease, `6`=ExpiryWriteOff, `7`=SupplierReturn, `8`=RecallWriteOff, `9`=ReceiptReversal |
| `QuantityDelta` | `decimal(18,3)` | no | Signed; never zero |
| `QuantityAfter` | `decimal(18,3)` | no | Batch balance immediately after this movement |
| `Reason` | `nvarchar(300)` | yes | Mandatory for types 4, 5, 6, 8 and 9 |
| `Reference` | `nvarchar(100)` | yes | e.g. invoice or purchase reference |
| `PerformedBy` | `nvarchar(150)` | no | Operator, until named users arrive in Milestone 5 |
| `OccurredAtUtc` | `datetimeoffset(3)` | no | |
| `BusinessDate` | `date` | no | Local pharmacy day, for date-range reports |

**Indexes and constraints**

| Name | Definition |
|---|---|
| `CK_StockMovements_QuantityDelta_NonZero` | `QuantityDelta <> 0` |
| `CK_StockMovements_QuantityAfter_NonNegative` | `QuantityAfter >= 0` |
| `CK_StockMovements_MovementType_Valid` | `MovementType BETWEEN 1 AND 9` |
| `CK_StockMovements_Reason_Required` | `MovementType NOT IN (4,5,6,8,9) OR (Reason IS NOT NULL AND LEN(Reason) > 0)` |
| `IX_StockMovements_Batch_Occurred` | Batch history |
| `IX_StockMovements_MedicineId` | Medicine history |
| `IX_StockMovements_BusinessDate_Type` | Daily sales/stock reports |

`MedicineId` is duplicated deliberately: it survives a future partition or archive of batches and
lets the reporting indexes stay covering. It is written by the domain code that also owns the batch,
so it cannot drift. No foreign key is declared because `Restrict` on two paths to the same graph
adds no safety the application does not already provide.

### 3.5 `Purchases`

A supplier invoice: the document that a delivery is received against. Receiving is the only operation
in MediStock that creates stock from outside the pharmacy, so the header carries the whole lifecycle
of that act — who confirmed it, when, and if it was later undone, why.

| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | `int IDENTITY` | no | PK |
| `SupplierId` | `int` | no | FK → `Suppliers.Id` (`Restrict`) |
| `InvoiceNumber` | `nvarchar(64)` | no | The supplier's own number; see the note below |
| `InvoiceDate` | `date` | no | Date printed on the supplier's invoice |
| `Notes` | `nvarchar(1000)` | yes | |
| `Status` | `int` | no | `1`=Draft, `2`=Received, `3`=Reversed, `4`=Cancelled |
| `ReceivedOn` | `date` | yes | Business date the goods were taken into stock |
| `ReceivedAtUtc` | `datetimeoffset(3)` | yes | |
| `ReceivedBy` | `nvarchar(150)` | yes | Operator, until named users arrive in Milestone 5 |
| `ClosedAtUtc` | `datetimeoffset(3)` | yes | When the purchase was cancelled or reversed |
| `ClosedBy` | `nvarchar(150)` | yes | Who cancelled or reversed it |
| `StatusReason` | `nvarchar(300)` | yes | Why it was cancelled or reversed |
| `CreatedAtUtc` | `datetimeoffset(3)` | no | |
| `UpdatedAtUtc` | `datetimeoffset(3)` | no | |
| `RowVersion` | `rowversion` | no | Concurrency token; see "Conditional lifecycle writes" below |

**Indexes and constraints**

| Name | Definition |
|---|---|
| `FK_Purchases_Suppliers_SupplierId` | `SupplierId → Suppliers.Id`, `Restrict` |
| `CK_Purchases_Status_Valid` | `Status BETWEEN 1 AND 4` |
| `CK_Purchases_Lifecycle_Consistent` | Each status is only legal together with the timestamps that justify it, see below |
| `IX_Purchases_InvoiceDate` | Date-range reports |
| `IX_Purchases_Status` | Draft and received lists |
| `IX_Purchases_SupplierId_InvoiceNumber` | Duplicate-invoice lookup |
| `RowVersion` | `rowversion`, read by EF Core as a concurrency token on every update of the header |

`CK_Purchases_Lifecycle_Consistent` is the constraint that makes the status column trustworthy:

```sql
([Status] = 1 AND [ReceivedOn] IS NULL AND [ReceivedAtUtc] IS NULL AND [ReceivedBy] IS NULL
             AND [ClosedAtUtc] IS NULL AND [ClosedBy] IS NULL AND [StatusReason] IS NULL)
OR ([Status] = 2 AND [ReceivedOn] IS NOT NULL AND [ReceivedAtUtc] IS NOT NULL AND [ReceivedBy] IS NOT NULL
             AND [ClosedAtUtc] IS NULL AND [ClosedBy] IS NULL AND [StatusReason] IS NULL)
OR ([Status] = 4 AND [ReceivedOn] IS NULL AND [ReceivedAtUtc] IS NULL AND [ReceivedBy] IS NULL
             AND [ClosedAtUtc] IS NOT NULL AND [ClosedBy] IS NOT NULL AND [StatusReason] IS NOT NULL)
OR ([Status] = 3 AND [ReceivedOn] IS NOT NULL AND [ReceivedAtUtc] IS NOT NULL AND [ReceivedBy] IS NOT NULL
             AND [ClosedAtUtc] IS NOT NULL AND [ClosedBy] IS NOT NULL AND [StatusReason] IS NOT NULL)
```

A received purchase must still name the operator and the date it was received, and a reversed one must
keep them: the correction does not erase the fact that the stock was once taken in. A draft cannot
carry a reason for closing, and a cancelled purchase cannot claim it was ever received.

**`InvoiceNumber` is deliberately not unique.** Two suppliers can legitimately print the same number,
and the same invoice can be entered twice by mistake — which is a conversation to have with the
operator, not a wall. `IX_Purchases_SupplierId_InvoiceNumber` backs the lookup that raises the warning
(`IPurchaseCatalog.FindByInvoiceAsync`). The protection against taking the same delivery into stock
twice is the lifecycle (`Receive` refuses anything that is not a `Draft`) **and**, for two operators
acting at the same moment, the rowversion below.

**Conditional lifecycle writes.** Every update of this header carries `WHERE [Id] = @id AND
[RowVersion] = @original`, because `RowVersion` is mapped as an EF Core concurrency token
(`PurchaseConfiguration`). A caller that read the purchase before another caller changed it therefore
matches no row, its transaction is never committed, and `EfPurchaseCatalog` returns
*"Another operator changed this purchase while you were working on it…"* instead of a stack trace.
Without it, a lifecycle transition is a blind `UPDATE … WHERE [Id] = @id` that overwrites whatever the
row now holds: two simultaneous receives of one invoice would each create their own batch for the same
line, and a `Cancel` whose read predated a committed receive would write `Cancelled` over `Received`,
leaving stock on the shelf that `CanReverse` then refuses to reverse. See
[ADR 0006](decisions/0006-context-lifetime-and-execution-strategy.md).

### 3.6 `PurchaseLines`

One medicine on the invoice, and — once received — the batch it created.

| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | `int IDENTITY` | no | PK |
| `PurchaseId` | `int` | no | FK → `Purchases.Id` (`Cascade`) |
| `MedicineId` | `int` | no | FK → `Medicines.Id` (`Restrict`) |
| `BatchNumber` | `nvarchar(64)` | no | As printed by the manufacturer |
| `ExpiryDate` | `date` | no | Must be after the receiving date |
| `Quantity` | `decimal(18,3)` | no | Ordered. Positive |
| `QuantityReceived` | `decimal(18,3)` | no | `0` while drafted, equal to `Quantity` from receiving onwards |
| `UnitCost` | `decimal(18,2)` | no | Becomes `MedicineBatches.PurchaseCost` |
| `SellingPrice` | `decimal(18,2)` | no | Becomes `MedicineBatches.SellingPrice` |
| `MedicineBatchId` | `int` | yes | FK → `MedicineBatches.Id` (`Restrict`); the batch this line created |

| Name | Definition |
|---|---|
| `UX_PurchaseLines_Purchase_Medicine_Batch` | Unique on `(PurchaseId, MedicineId, BatchNumber)` |
| `CK_PurchaseLines_Quantity_Positive` | `Quantity > 0` |
| `CK_PurchaseLines_QuantityReceived_Within` | `QuantityReceived >= 0 AND QuantityReceived <= Quantity` |
| `CK_PurchaseLines_Costs_NonNegative` | `UnitCost >= 0 AND SellingPrice >= 0` |
| `IX_PurchaseLines_MedicineId` | Medicine purchase history |
| `IX_PurchaseLines_MedicineBatchId` | Reverse a purchase from its batches |

`QuantityReceived` is separate from `Quantity` even though receiving is currently all-or-nothing. It
costs one column and means partial receiving can be added later as a behaviour change instead of a
schema change. `MedicineBatchId` is **kept after a reversal** so the purchase stays traceable to the
stock it once brought in.

A zero `UnitCost` is allowed — suppliers do send free items, and the line still has to be counted.

## 4. Invariants

These must hold after every committed transaction. Each is enforced by the constraint named, and
each is asserted by an integration test.

| # | Invariant | Enforced by |
|---|---|---|
| I1 | Quantity on hand is never negative | `CK_MedicineBatches_QuantityOnHand_NonNegative` |
| I2 | Quantity on hand never exceeds what was received | `CK_MedicineBatches_QuantityOnHand_NonNegative` |
| I3 | A batch cannot be received twice with the same identity | `Id` PK |
| I4 | Expiry is always after receipt | `CK_MedicineBatches_ExpiryAfterReceipt` |
| I5 | Prices and costs are never negative | `CK_MedicineBatches_Prices_NonNegative` |
| I6 | A stock movement always changes the balance | `CK_StockMovements_QuantityDelta_NonZero` |
| I7 | A movement that reduces stock records why, when the type demands it | `CK_StockMovements_Reason_Required` |
| I8 | A medicine cannot be deleted while batches reference it | FK `Restrict` |
| I9 | A supplier cannot be deleted while batches reference it | FK `Restrict` |
| I10 | A batch cannot be deleted while movements reference it | FK `Restrict` |
| I11 | A barcode is unique when present | `UX_Medicines_Barcode` (filtered unique) |
| I12 | Supplier name and GSTIN are unique | `UX_Suppliers_Name`, `UX_Suppliers_Gstin` |
| I13 | `QuantityOnHand` equals the sum of `QuantityDelta` for the batch | Application + reconciliation query (§5) |
| I14 | A purchase status is always consistent with its own timestamps | `CK_Purchases_Lifecycle_Consistent` |
| I15 | A purchase line is never received for more than was ordered | `CK_PurchaseLines_QuantityReceived_Within` |
| I16 | One purchase cannot carry the same medicine and batch number twice | `UX_PurchaseLines_Purchase_Medicine_Batch` |
| I17 | A line points at a batch that exists, and a purchase at a supplier that exists | FKs `Restrict` |
| I18 | A lifecycle transition never overwrites a state another operation has already committed | `Purchases.RowVersion` + conditional update in `EfPurchaseCatalog` |

I13 is the only invariant the schema cannot express, because it spans rows. It is met by (a) the domain
type refusing to mutate a balance without recording a movement and (b) the reconciliation query below,
which the reconciliation script surfaces as a health check. I18 is conditional rather than declarative:
the token is a column, but the check lives in the `WHERE` clause the token generates.

## 5. Reconciliation and reporting queries

Cached balances versus the ledger — expected to return **no rows**:

```sql
SELECT b.Id, b.BatchNumber, b.QuantityOnHand AS Cached, COALESCE(m.Total, 0) AS Ledger
FROM MedicineBatches AS b
OUTER APPLY (
    SELECT SUM(QuantityDelta) AS Total
    FROM StockMovements
    WHERE MedicineBatchId = b.Id
) AS m
WHERE b.QuantityOnHand <> COALESCE(m.Total, 0);
```

Sellable stock for a medicine on a given business date (used by the dashboard and, later, by the
point of sale):

```sql
SELECT b.Id, b.MedicineId, b.BatchNumber, b.ExpiryDate, b.QuantityOnHand, b.SellingPrice
FROM MedicineBatches AS b
JOIN Medicines AS med ON med.Id = b.MedicineId
WHERE b.MedicineId = @medicineId
  AND med.IsActive = 1
  AND b.Status = 1
  AND b.QuantityOnHand > 0
  AND b.ExpiryDate >= @businessDate      -- expiry date is the last dispensable day
ORDER BY b.ExpiryDate, b.Id;               -- FEFO
```

Near-expiry batches, excluding anything already expired:

```sql
SELECT b.*, med.DisplayName
FROM MedicineBatches AS b
JOIN Medicines AS med ON med.Id = b.MedicineId
WHERE b.QuantityOnHand > 0
  AND b.Status = 1
  AND b.ExpiryDate >= @businessDate
  AND b.ExpiryDate <= DATEADD(day, @nearExpiryDays, @businessDate)
ORDER BY b.ExpiryDate;
```

## 6. Costing and valuation

Inventory valuation uses the **actual cost of each batch** (specific identification):

```sql
SELECT SUM(QuantityOnHand * PurchaseCost)
FROM MedicineBatches
WHERE QuantityOnHand > 0;
```

Each batch keeps the purchase cost it was actually received at, so the total is the sum of what the
pharmacy really paid for the units still on hand. No weighted average is computed and no cost layer
is merged: this is the most faithful method available given that the schema already stores
per-batch cost, and it is the one a pharmacist can reconcile against supplier invoices line by line.

The figure **includes batches that are no longer sellable** (expired, written off, recalled) as long
as they still hold quantity, because they are still owned stock. Reports must therefore show a
sellable/sell-through split rather than one number. See
[ADR 0003](decisions/0003-inventory-valuation.md) for the full reasoning, worked example and the
follow-up work this creates.

Because valuation reads `PurchaseCost` and quantity from `MedicineBatches`, it depends on invariant
I13 (§4): if a cached balance ever drifted from the ledger, the valuation would be wrong too. The
reconciliation query in §5 is the guard.

## 7. Migrations

| Migration | Purpose |
|---|---|
| `20261015214039_InitialCreate` | `Medicines`, `Suppliers`, `MedicineBatches`, `StockMovements` and every constraint above |
| `20261015215632_PurchasingAndReceiving` | `Purchases`, `PurchaseLines` and the constraints in §3.5–3.6 |
| `20261015222213_PurchaseRowVersion` | `Purchases.RowVersion`, the concurrency token behind invariant I18 |

```powershell
dotnet ef migrations add <Name> --project src\MediStock.Infrastructure --startup-project src\MediStock.Desktop
dotnet ef database update      --project src\MediStock.Infrastructure --startup-project src\MediStock.Desktop
```

A fresh database can be produced from migrations alone — verified by dropping `MediStock_Test` and
re-running `dotnet ef database update`. `MediStockDbContextFactory` provides the design-time
context so `dotnet ef` never has to start the WPF application.

## 8. Planned tables

Not yet modelled, listed so the migration plan stays legible. Each will arrive with its own ADR
where the plan's shape is ambiguous.

| Milestone | Tables |
|---|---|
| 3 | `Sales`, `SaleLines`, `SaleLineBatchAllocations`, `Payments`, `InvoiceSequences` |
| 4 | `SalesReturns`, `SalesReturnLines`, `SupplierReturns`, `SupplierReturnLines` |
| 5 | `Users`, `Roles`, `UserRoles`, `AuditLogs`, `ApplicationSettings` |

Two departures from the plan's original list are deliberate and recorded in
[ADR 0005](decisions/0005-no-purchase-orders-or-goods-receipts.md): Milestone 2 ships **no**
`PurchaseHeaders`, `GoodsReceipts`, `GoodsReceiptLines`, `StockMovementReasons` or
`StockAdjustments` tables. The first three collapsed into `Purchases` and `PurchaseLines`, and the
last two are not needed while every adjustment already carries its own typed movement and reason
(§3.4).

`InvoiceSequences` will use a transactional counter row (or a SQL `SEQUENCE`) so invoice numbers stay
unique under concurrency — never `MAX(Number) + 1`.
