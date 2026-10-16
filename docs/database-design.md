# MediStock database design

The schema currently implements the catalogue, supplier, inventory and point-of-sale foundations
(Milestones 0–3). Returns, identity and audit tables are described in [roadmap.md](roadmap.md) and
will be added by later migrations.

Everything in this document is the **current** state of `20261015224739_MedicineTaxRatePrecision`. Schema
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
| `TaxRatePercent` | `decimal(5,2)` | no | Percentage applied at the till, `0`–`100`. Added by `PointOfSale`; narrowed to `(5,2)` by `MedicineTaxRatePrecision` |
| `IsActive` | `bit` | no | Inactive medicines stay for history but cannot be sold |
| `CreatedAtUtc` | `datetimeoffset(3)` | no | |
| `UpdatedAtUtc` | `datetimeoffset(3)` | no | |

**Indexes and constraints**

- `UX_Medicines_Barcode` — unique, filtered `WHERE [Barcode] IS NOT NULL`. Two medicines may both
  have *no* barcode, but not the same barcode.
- `IX_Medicines_DisplayName`, `IX_Medicines_GenericName` — search support.
- `CK_Medicines_ReorderThreshold_NonNegative`.
- `CK_Medicines_TaxRate_Range` — `TaxRatePercent >= 0 AND TaxRatePercent <= 100`, mirroring
  `CK_SaleLines_TaxRate_Range` and `Medicine.MaxTaxRatePercent`.

A tax rate is a percentage, not money, so it is `decimal(5,2)`: two decimals is enough for any slab and
`decimal(18,2)` would have let a fat-fingered rate through. The rate is copied onto each sale line at
checkout, so editing the catalogue never restates an issued invoice.

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

### 3.7 `Sales`

One completed invoice. A sale is created completed, in one act, and is never edited: every money
column is stored rather than derived, because an invoice is a historical document that must reprint
unchanged after a tax rate, a catalogue name or a rounding policy has moved on.

| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | `int IDENTITY` | no | PK |
| `InvoiceNumber` | `nvarchar(32)` | no | As issued to the customer, e.g. `INV-2026-000123`; never reused |
| `BusinessDate` | `date` | no | Local pharmacy day the sale counts towards |
| `SoldAtUtc` | `datetimeoffset(3)` | no | When it was rung up |
| `SoldBy` | `nvarchar(150)` | no | Operator name; becomes a user reference in Milestone 5 |
| `CustomerName` | `nvarchar(150)` | yes | |
| `CustomerPhone` | `nvarchar(32)` | yes | Only ever set together with a name |
| `Notes` | `nvarchar(500)` | yes | |
| `Status` | `int` | no | `1`=Completed, `2`=Returned, `3`=Cancelled |
| `SubTotal` | `decimal(18,2)` | no | Sum of the line gross amounts |
| `DiscountTotal` | `decimal(18,2)` | no | Sum of the line discounts |
| `TaxableTotal` | `decimal(18,2)` | no | `SubTotal` − `DiscountTotal` |
| `TaxTotal` | `decimal(18,2)` | no | Sum of the line tax amounts |
| `Total` | `decimal(18,2)` | no | `TaxableTotal` + `TaxTotal`; what the payments settle |
| `CostTotal` | `decimal(18,2)` | no | Cost of the goods sold, for margin reporting only |
| `CreatedAtUtc` | `datetimeoffset(3)` | no | |
| `CancelledAtUtc` | `datetimeoffset(3)` | yes | Set only when the invoice is cancelled |
| `CancelledBy` | `nvarchar(150)` | yes | Set only when the invoice is cancelled |
| `CancellationReason` | `nvarchar(300)` | yes | Set only when the invoice is cancelled |
| `RowVersion` | `rowversion` | no | Concurrency token; see below |

| Name | Definition |
|---|---|
| `CK_Sales_Status_Valid` | `Status BETWEEN 1 AND 3` |
| `CK_Sales_Totals_Reconcile` | All six totals `>= 0`, `SubTotal = TaxableTotal + DiscountTotal`, `Total = TaxableTotal + TaxTotal`, `DiscountTotal <= SubTotal` |
| `CK_Sales_Customer_Phone_With_Name` | `CustomerPhone IS NULL OR CustomerName IS NOT NULL` |
| `CK_Sales_Cancellation_Consistent` | A cancelled invoice carries `CancelledAtUtc`, `CancelledBy` and `CancellationReason`; an uncancelled one carries none of them |
| `UX_Sales_InvoiceNumber` | Unique on `(InvoiceNumber)` |
| `IX_Sales_BusinessDate` | Daily takings and the sales history |
| `IX_Sales_SoldAtUtc` | Recent sales, ordered by time |

`CK_Sales_Totals_Reconcile` is the constraint that makes a stored invoice check itself. Because the
totals are *sums of the stored, already-rounded line values* rather than a rounding of an unrounded
sum, the lines printed on an invoice always add up to the totals printed below them.

`RowVersion` arrived with Milestone 4, which is the first code that updates a sale: `Status` moves to
`Returned` once nothing is left to bring back, and to `Cancelled` when an invoice that has been fully
returned is voided. Those writes are conditional on the token (see
[ADR 0006](decisions/0006-context-lifetime-and-execution-strategy.md)), because an unconditional
`UPDATE … WHERE [Id] = @id` would let a losing writer overwrite a winner. Nothing about the *money* of
an invoice is ever updated: the four totals above are written once, at insertion, and never again.

### 3.8 `SaleLines`

One charged line, with everything the printed document needs copied onto it at the moment of sale.

| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | `int IDENTITY` | no | PK |
| `SaleId` | `int` | no | FK → `Sales.Id` (`Cascade`) |
| `LineNumber` | `int` | no | Position on the invoice, consecutive from `1` |
| `MedicineId` | `int` | no | FK → `Medicines.Id` (`Restrict`) |
| `MedicineName` | `nvarchar(200)` | no | The catalogue name as it stood at the time of sale |
| `Quantity` | `decimal(18,3)` | no | Positive |
| `UnitPrice` | `decimal(18,2)` | no | Price per unit actually charged |
| `UnitCost` | `decimal(18,2)` | no | Quantity-weighted average cost of the batches consumed |
| `GrossAmount` | `decimal(18,2)` | no | `round(Quantity × UnitPrice)` |
| `DiscountAmount` | `decimal(18,2)` | no | Absolute amount, never more than the gross |
| `TaxableAmount` | `decimal(18,2)` | no | `GrossAmount` − `DiscountAmount` |
| `TaxRatePercent` | `decimal(5,2)` | no | The rate applied, snapshotted |
| `TaxAmount` | `decimal(18,2)` | no | `round(TaxableAmount × TaxRatePercent ÷ 100)` |
| `LineTotal` | `decimal(18,2)` | no | `TaxableAmount` + `TaxAmount` |
| `ReturnedQuantity` | `decimal(18,3)` | no | Units of this line that customers have brought back; starts at `0` |
| `ReturnedAmount` | `decimal(18,2)` | no | Money refunded against this line; starts at `0` |
| `ReturnedTax` | `decimal(18,2)` | no | The tax portion of those refunds; starts at `0` |
| `ReturnedCost` | `decimal(18,2)` | no | The cost portion of those refunds, for margin netting; starts at `0` |
| `RowVersion` | `rowversion` | no | Concurrency token over the returned columns; see below |

| Name | Definition |
|---|---|
| `CK_SaleLines_Quantity_Positive` | `Quantity > 0` |
| `CK_SaleLines_Money_NonNegative` | Every money column and `TaxRatePercent` `>= 0` |
| `CK_SaleLines_TaxRate_Range` | `TaxRatePercent <= 100` |
| `CK_SaleLines_Discount_Within_Gross` | `DiscountAmount <= GrossAmount` |
| `CK_SaleLines_LineNumber_Positive` | `LineNumber > 0` |
| `CK_SaleLines_Amounts_Consistent` | `GrossAmount = TaxableAmount + DiscountAmount AND LineTotal = TaxableAmount + TaxAmount` |
| `CK_SaleLines_Returned_Within_Sold` | `ReturnedQuantity >= 0 AND ReturnedQuantity <= Quantity` |
| `CK_SaleLines_ReturnedAmount_Within_LineTotal` | `ReturnedAmount >= 0 AND ReturnedAmount <= LineTotal` |
| `CK_SaleLines_ReturnedTax_Within_Tax` | `ReturnedTax >= 0 AND ReturnedTax <= TaxAmount` |
| `CK_SaleLines_ReturnedCost_Within_LineCost` | `ReturnedCost >= 0 AND ReturnedCost <= round(Quantity × UnitCost, 2)` |
| `UX_SaleLines_Sale_LineNumber` | Unique on `(SaleId, LineNumber)` |
| `IX_SaleLines_MedicineId` | Sales-by-medicine reporting |

The medicine is referenced for reporting but never deleted, so a historic invoice keeps pointing at
the catalogue entry it was written against.

The four `Returned…` columns are the cumulative record of what has come back off this line, and they
are the only columns on an invoice line that a return writes. They are what makes the double-refund
check possible (invariant I30), what lets the sales report show a line gross *and* net of returns, and
what tells the counter that a line has nothing left to bring back. `CK_SaleLines_ReturnedCost_Within_LineCost`
spells out `round(Quantity × UnitCost, 2)` rather than naming a computed column, so that the bound it
compares against is the same rounding the domain applies to `LineCost`. The `RowVersion` token makes the
update conditional: two tills refunding the last unit of the same line cannot both commit, and the loser
is refused instead of both refunds standing.

### 3.9 `SaleLineBatchAllocations`

How much of one line came out of one batch. This is the join that makes a sale traceable to the stock
it consumed, and it is what a future return is credited against.

| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | `int IDENTITY` | no | PK |
| `SaleLineId` | `int` | no | FK → `SaleLines.Id` (`Cascade`) |
| `MedicineBatchId` | `int` | no | FK → `MedicineBatches.Id` (`Restrict`) |
| `BatchNumber` | `nvarchar(64)` | no | As printed on the pack at the time of sale |
| `ExpiryDate` | `date` | no | The batch's expiry as it stood at the time of sale |
| `Quantity` | `decimal(18,3)` | no | Positive; a sale only ever removes stock |
| `ReturnedQuantity` | `decimal(18,3)` | no | Units of this allocation that have been returned; starts at `0` |
| `RowVersion` | `rowversion` | no | Concurrency token over `ReturnedQuantity` |

| Name | Definition |
|---|---|
| `CK_SaleLineBatchAllocations_Quantity_Positive` | `Quantity > 0` |
| `CK_SaleLineBatchAllocations_Returned_Within_Allocated` | `ReturnedQuantity >= 0 AND ReturnedQuantity <= Quantity` |
| `UX_SaleLineBatchAllocations_Line_Batch` | Unique on `(SaleLineId, MedicineBatchId)` |
| `IX_SaleLineBatchAllocations_MedicineBatchId` | Answers "what has ever been sold out of this batch?" for expiry and recall investigations |

The batch number and expiry date are copied rather than joined, so reprinting an invoice years later
shows what the label actually said. The foreign key is `Restrict`, not `Cascade`: a batch is never
deleted, and if one ever were, the invoice that recorded taking stock from it must not quietly lose
that fact. The allocations of a line must sum to its `Quantity` — that is enforced by
`SaleLine.Create` rather than by a constraint, because it spans rows; see invariant I25.

This is the table a return is credited against, which is why it carries its own returned quantity and
its own `RowVersion`: an allocation is the finest-grained thing that knows *which batch* a unit came
out of, so it is the only place where "how much of this batch can still come back?" can be answered
without walking the returns (§3.13).

### 3.10 `Payments`

One settlement against an invoice. Several payments may settle one sale, which is how a split payment
(part cash, part card) is recorded.

| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | `int IDENTITY` | no | PK |
| `SaleId` | `int` | no | FK → `Sales.Id` (`Cascade`) |
| `Method` | `int` | no | `1`=Cash, `2`=Card, `3`=Upi, `4`=Other |
| `Amount` | `decimal(18,2)` | no | The amount **settled** by this method, not the amount tendered |
| `Reference` | `nvarchar(100)` | yes | Card or UPI reference, or any note the operator needs |
| `PaidAtUtc` | `datetimeoffset(3)` | no | |

| Name | Definition |
|---|---|
| `CK_Payments_Amount_Positive` | `Amount > 0` |
| `CK_Payments_Method_Valid` | `Method BETWEEN 1 AND 4` |
| `IX_Payments_SaleId_Method` | Takings by payment method |

`Amount > 0` is what makes a refund impossible to express as a payment: it has to be a return
document (Milestone 4) that references the original sale and line. Change given at the counter is a
till concern and is not stored — the sum of the payments must equal the invoice total exactly, and
that can only be true if change is not part of the invoice.

### 3.11 `InvoiceSequences`

The counter behind invoice numbers: one row per numbering period, holding the last number issued.

| Column | Type | Null | Notes |
|---|---|---|---|
| `SequenceKey` | `nvarchar(32)` | no | PK, the numbering period, e.g. `INV-2026` |
| `NextNumber` | `bigint` | no | The last number issued in this period |
| `CreatedAtUtc` | `datetimeoffset(3)` | no | |
| `UpdatedAtUtc` | `datetimeoffset(3)` | no | |

| Name | Definition |
|---|---|
| `CK_InvoiceSequences_NextNumber_NonNegative` | `NextNumber >= 0` |

The key is derived from the business date (`InvoiceSequence.KeyFor`), so the numbering period is the
calendar year and the first invoice of 2027 is `INV-2027-000001`. Returns use the same table with the
`RET-` prefix (`InvoiceSequence.ReturnsPrefix`), so the first return of 2026 is `RET-2026-000001`. One
table, not two: a document number has to be unique and issued in one act with the write that consumes
it, and a second counter would only be a second thing to get wrong. Numbers are consumed by one atomic
statement inside the transaction that writes the document, never by a read followed by a write:

```sql
MERGE INTO [dbo].[InvoiceSequences] WITH (HOLDLOCK) AS [target]
USING (SELECT @key AS [SequenceKey]) AS [source]
    ON [target].[SequenceKey] = [source].[SequenceKey]
WHEN MATCHED THEN
    UPDATE SET [NextNumber] = [target].[NextNumber] + 1, [UpdatedAtUtc] = @now
WHEN NOT MATCHED THEN
    INSERT ([SequenceKey], [NextNumber], [CreatedAtUtc], [UpdatedAtUtc])
    VALUES (@key, 1, @now, @now)
OUTPUT INSERTED.[NextNumber];
```

The first till holds an exclusive lock on the row until its transaction ends, and the second sees the
advanced value, so two tills checking out at the same instant cannot both be handed the same number.
There is deliberately **no concurrency token** on this row: it is never read before it is advanced, so
a token would add a second failure mode without closing a race that the statement already closes. A
sale that fails to commit releases its number, so a cancelled sale leaves a gap; `Sales`' unique index
is the second line of defence. Never `MAX(Number) + 1`.

### 3.12 `SalesReturns`

One customer return: a document of its own, not an edit of the invoice it credits. It records who took
the medicine back, when, why, and by which method the money went out, and it keeps the invoice's own
figures untouched so a reprint still says what it said on the day of sale.

| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | `int IDENTITY` | no | PK |
| `ReturnNumber` | `nvarchar(32)` | no | As given to the customer, e.g. `RET-2026-000123`; never reused |
| `SaleId` | `int` | no | FK → `Sales.Id` (`Restrict`); the invoice being credited |
| `BusinessDate` | `date` | no | Local pharmacy day the return counts towards; never before the invoice's |
| `ReturnedAtUtc` | `datetimeoffset(3)` | no | When it was taken back |
| `ReturnedBy` | `nvarchar(150)` | no | Operator name; becomes a user reference in Milestone 5 |
| `Reason` | `nvarchar(300)` | no | Required, and not blank |
| `Notes` | `nvarchar(500)` | yes | |
| `RefundMethod` | `int` | no | `1`=Cash, `2`=Card, `3`=Upi, `4`=Other |
| `RefundTotal` | `decimal(18,2)` | no | Sum of the line refunds on this document |
| `CreatedAtUtc` | `datetimeoffset(3)` | no | |

| Name | Definition |
|---|---|
| `CK_SalesReturns_RefundTotal_NonNegative` | `RefundTotal >= 0` |
| `CK_SalesReturns_RefundMethod_Valid` | `RefundMethod BETWEEN 1 AND 4` |
| `CK_SalesReturns_Reason_NotBlank` | `LEN(LTRIM(RTRIM([Reason]))) > 0` |
| `UX_SalesReturns_ReturnNumber` | Unique on `(ReturnNumber)` |
| `IX_SalesReturns_BusinessDate` | Returns are reported by business date, the same axis daily takings use |
| `IX_SalesReturns_SaleId` | Everything that has come back off one invoice |

The foreign key to `Sales` is `Restrict`, not `Cascade`: deleting an invoice must not be able to take
the record of money refunded against it along with it. Nothing here is derived: `RefundTotal` is the
sum of the stored line refunds, so the printed document adds up the same way an invoice does. See
[ADR 0008](decisions/0008-sales-returns-and-stock-disposition.md) for why a refund is not a `Payment`
and why a cancellation is not a return.

### 3.13 `SalesReturnLines`

One returned quantity, credited against the invoice line it came from and the batch it went back to (or
did not). The disposition is what decides whether the shelf gains stock back.

| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | `int IDENTITY` | no | PK |
| `SalesReturnId` | `int` | no | FK → `SalesReturns.Id` (`Cascade`) |
| `SaleLineId` | `int` | no | FK → `SaleLines.Id` (`Restrict`); what was charged and is being credited |
| `MedicineBatchId` | `int` | no | FK → `MedicineBatches.Id` (`Restrict`); which batch came back |
| `LineNumber` | `int` | no | Position on the return document, consecutive from `1` |
| `Quantity` | `decimal(18,3)` | no | Positive; within what is left of the allocation |
| `RefundAmount` | `decimal(18,2)` | no | Share of the line total given back |
| `TaxAmount` | `decimal(18,2)` | no | Share of the line tax given back |
| `CostAmount` | `decimal(18,2)` | no | Share of the line cost given back, so margin nets out |
| `Disposition` | `int` | no | `1`=Restock, `2`=Quarantine, `3`=Destroy |
| `Notes` | `nvarchar(500)` | yes | |

| Name | Definition |
|---|---|
| `CK_SalesReturnLines_LineNumber_Positive` | `LineNumber > 0` |
| `CK_SalesReturnLines_Quantity_Positive` | `Quantity > 0` |
| `CK_SalesReturnLines_RefundAmount_NonNegative` | `RefundAmount >= 0` |
| `CK_SalesReturnLines_Refund_Components_NonNegative` | `TaxAmount >= 0 AND CostAmount >= 0` |
| `CK_SalesReturnLines_Disposition_Valid` | `Disposition BETWEEN 1 AND 3` |
| `UX_SalesReturnLines_Return_LineNumber` | Unique on `(SalesReturnId, LineNumber)` |
| `IX_SalesReturnLines_SaleLineId` | What has come back off one invoice line |
| `IX_SalesReturnLines_MedicineBatchId` | How much of one batch has been returned |

Both parents are `Restrict`: the invoice line a refund is credited against, and the batch it names, are
never removed from under the return. `Disposition` is stored as an integer for the same reason
`Status` is: a value that is written into historical records must not be renumbered by inserting an
enum member later. The bounds between a return line and the line it credits — that the quantity is
within the returnable amount, and that the money is within what was charged — span tables and are
enforced by `SalesReturn.Record` (invariants I30–I33), then verified by the reconciliation queries in
§5.

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
| I19 | A line's money is the sum of its own rounded steps | `CK_SaleLines_Amounts_Consistent` |
| I20 | An invoice's stored totals equal the sum of its stored lines | `CK_Sales_Totals_Reconcile` + `SaleTotals.From` |
| I21 | A discount never exceeds the line value it discounts | `CK_SaleLines_Discount_Within_Gross` |
| I22 | Every payment is positive and uses a known method | `CK_Payments_Amount_Positive`, `CK_Payments_Method_Valid` |
| I23 | The payments of an invoice settle its total exactly | `Sale.Complete` (application) |
| I24 | An invoice number identifies exactly one sale, and is never reused | `UX_Sales_InvoiceNumber` + `MERGE … WITH (HOLDLOCK)` on `InvoiceSequences` |
| I25 | The allocations of a line are positive and sum to its quantity | `CK_SaleLineBatchAllocations_Quantity_Positive` + `SaleLine.Create` |
| I26 | One line draws on a given batch at most once | `UX_SaleLineBatchAllocations_Line_Batch` |
| I27 | Sale lines are numbered consecutively from one within their sale | `UX_SaleLines_Sale_LineNumber` + `Sale.Complete` |
| I28 | A customer phone number cannot be recorded without a name | `CK_Sales_Customer_Phone_With_Name` |
| I29 | Every unit sold is matched by a stock movement, and neither can commit without the other | `EfSaleCatalog` checkout order + reconciliation query (§5) |
| I30 | A line never returns more than it sold, and never refunds more than it charged | `CK_SaleLines_Returned_Within_Sold`, `CK_SaleLines_ReturnedAmount_Within_LineTotal`, `CK_SaleLines_ReturnedTax_Within_Tax`, `CK_SaleLines_ReturnedCost_Within_LineCost` |
| I31 | An allocation never returns more than it allocated | `CK_SaleLineBatchAllocations_Returned_Within_Allocated` |
| I32 | A return line credits a line that really did sell that batch | FKs `Restrict` + `SalesReturn.Record` + reconciliation query (§5) |
| I33 | A return's refund is exactly the sum of its line refunds | `SalesReturn.Record` + reconciliation query (§5) |
| I34 | A return names a reason, an operator and a known refund method | `CK_SalesReturns_Reason_NotBlank`, `CK_SalesReturns_RefundMethod_Valid` |
| I35 | Every returned line has a known disposition | `CK_SalesReturnLines_Disposition_Valid` |
| I36 | Returned stock comes back only where a line says `Restock`, and every restock has its movement | `EfSaleReturnCatalog` + reconciliation query (§5) |
| I37 | A return is never dated before the invoice it credits, and a cancelled invoice takes no return | `SalesReturn.Record` |
| I38 | An invoice is marked returned only when nothing is left to bring back, and cancelled only once it has been | `Sale.Record` / `Sale.Cancel` + `CK_Sales_Status_Valid` |
| I39 | A cancelled invoice records who cancelled it, when and why | `CK_Sales_Cancellation_Consistent` |
| I40 | Two returns cannot both refund the same unit | `Sales`, `SaleLines` and `SaleLineBatchAllocations` `RowVersion` + conditional write |
| I41 | A return number identifies exactly one return, and is never reused | `UX_SalesReturns_ReturnNumber` + `MERGE … WITH (HOLDLOCK)` on `InvoiceSequences` |

I13 is the only invariant the schema cannot express, because it spans rows. It is met by (a) the domain
type refusing to mutate a balance without recording a movement and (b) the reconciliation query below,
which the reconciliation script surfaces as a health check. I18 is conditional rather than declarative:
the token is a column, but the check lives in the `WHERE` clause the token generates.

I23, I25 and I29 also span rows, and each is enforced by the shape of the one transaction that writes
them: the payments are summed against the total before the sale object is built, the allocations are
checked against the quantity as each line is priced, and the stock movements are written in the same
transaction as the sale and its quantity decrements. None of the three can be expressed as a check
constraint, because all three compare rows the statement that creates them is still writing.

The return invariants follow the same split. I30 and I31 are declarative, because a line knows both what
it sold and what has come back. I32, I33 and I36 compare a return against the invoice it credits and the
batch it names, and I38 spans an invoice and every return against it; each one is enforced by the object
that plans the operation — `SalesReturn.Record` validates the whole basket before applying any of it, and
`EfSaleReturnCatalog` writes the refund, the quantity credits and the one restock movement inside a
single transaction — and then checked by §5. I40 is conditional rather than declarative, in the same way
as I18: the token is a column, but the check lives in the `WHERE` clause it generates.

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

Sellable stock for a medicine on a given business date (used by the dashboard and by the point of sale):

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

Every allocation on an invoice against the movements that took the stock out — expected to return
**no rows**. Quantities are aggregated per sale and batch, because two lines of one invoice may draw
on the same batch and produce a single movement between them:

```sql
WITH Allocated AS (
    SELECT l.SaleId AS SaleId, a.MedicineBatchId AS MedicineBatchId, SUM(a.Quantity) AS Quantity
    FROM SaleLineBatchAllocations AS a
    JOIN SaleLines AS l ON l.Id = a.SaleLineId
    GROUP BY l.SaleId, a.MedicineBatchId
),
Moved AS (
    SELECT TRY_CONVERT(int, SUBSTRING(m.Reference, 6, 20)) AS SaleId,
           m.MedicineBatchId AS MedicineBatchId,
           SUM(-m.QuantityDelta) AS Quantity
    FROM StockMovements AS m
    WHERE m.MovementType = 2 AND m.Reference LIKE 'SALE-%'
    GROUP BY TRY_CONVERT(int, SUBSTRING(m.Reference, 6, 20)), m.MedicineBatchId
)
SELECT COALESCE(allocated.SaleId, moved.SaleId) AS SaleId,
       COALESCE(allocated.MedicineBatchId, moved.MedicineBatchId) AS MedicineBatchId,
       allocated.Quantity AS Allocated, moved.Quantity AS Moved
FROM Allocated AS allocated
FULL OUTER JOIN Moved AS moved
    ON moved.SaleId = allocated.SaleId AND moved.MedicineBatchId = allocated.MedicineBatchId
WHERE allocated.Quantity IS NULL
   OR moved.Quantity IS NULL
   OR allocated.Quantity <> moved.Quantity;
```

What a return line recorded against an invoice line must be the same thing the invoice line says has come
back, in units and in money — including the tax and the cost carried across with the refund. Expected to
return **no rows**:

```sql
WITH Returned AS (
    SELECT SaleLineId,
           SUM(Quantity) AS Quantity,
           SUM(RefundAmount) AS RefundAmount,
           SUM(TaxAmount) AS TaxAmount,
           SUM(CostAmount) AS CostAmount
    FROM SalesReturnLines
    GROUP BY SaleLineId
)
SELECT l.Id AS SaleLineId, s.InvoiceNumber, l.LineNumber
FROM SaleLines AS l
JOIN Sales AS s ON s.Id = l.SaleId
LEFT JOIN Returned AS r ON r.SaleLineId = l.Id
WHERE l.ReturnedQuantity <> COALESCE(r.Quantity, 0)
   OR l.ReturnedAmount <> COALESCE(r.RefundAmount, 0)
   OR l.ReturnedTax <> COALESCE(r.TaxAmount, 0)
   OR l.ReturnedCost <> COALESCE(r.CostAmount, 0);
```

The same query shape guards the batch: a return line's `MedicineBatchId` must be a batch the line
actually sold, which the `UX_SaleLineBatchAllocations_Line_Batch` index answers. Two further checks live
in `scripts\dev\check-reconciliation.ps1`: a return's `RefundTotal` versus the sum of its own lines (and
every return belonging to an invoice that has lines on it), and the restock movements versus the lines
that say `Restock` — one aggregated movement per return and batch, nothing for a line that was
quarantined or destroyed, and nothing that no line accounts for. Expected to return **no rows**:

```sql
WITH Restocked AS (
    SELECT l.SalesReturnId AS SalesReturnId, l.MedicineBatchId AS MedicineBatchId, SUM(l.Quantity) AS Quantity
    FROM SalesReturnLines AS l
    WHERE l.Disposition = 1
    GROUP BY l.SalesReturnId, l.MedicineBatchId
),
Moved AS (
    SELECT TRY_CONVERT(int, SUBSTRING(m.Reference, 5, 20)) AS SalesReturnId,
           m.MedicineBatchId AS MedicineBatchId,
           SUM(m.QuantityDelta) AS Quantity
    FROM StockMovements AS m
    WHERE m.MovementType = 3 AND m.Reference LIKE 'RET-%'
    GROUP BY TRY_CONVERT(int, SUBSTRING(m.Reference, 5, 20)), m.MedicineBatchId
)
SELECT COALESCE(restocked.SalesReturnId, moved.SalesReturnId) AS SalesReturnId,
       COALESCE(restocked.MedicineBatchId, moved.MedicineBatchId) AS MedicineBatchId,
       restocked.Quantity AS Restocked, moved.Quantity AS Moved
FROM Restocked AS restocked
FULL OUTER JOIN Moved AS moved
    ON moved.SalesReturnId = restocked.SalesReturnId
   AND moved.MedicineBatchId = restocked.MedicineBatchId
WHERE restocked.Quantity IS NULL
   OR moved.Quantity IS NULL
   OR restocked.Quantity <> moved.Quantity;
```

`MovementType = 3` is `SalesReturn` and `Disposition = 1` is `Restock`. The reference is parsed rather
than joined because a stock movement carries no foreign key to the document that caused it — the ledger
is intentionally independent of the workflow tables, so that a movement remains readable even if the
document that produced it is ever archived.

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
| `20261015223056_PointOfSale` | `Sales`, `SaleLines`, `SaleLineBatchAllocations`, `Payments`, `InvoiceSequences`, `Medicines.TaxRatePercent` and the constraints in §3.7–3.11 |
| `20261015224739_MedicineTaxRatePrecision` | `Medicines.TaxRatePercent` to `decimal(5,2)` plus `CK_Medicines_TaxRate_Range`; no table is added |
| `20261015225936_SalesReturnsAndStockDisposition` | `SalesReturns`, `SalesReturnLines`, the returned-quantity and returned-money columns on `SaleLines` and `SaleLineBatchAllocations` with their bounds, `RowVersion` on `Sales`, `SaleLines` and `SaleLineBatchAllocations`, and the cancellation audit columns on `Sales`; §3.7–3.9 and §3.12–3.13 |

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
| 4 | `SalesReturns` and `SalesReturnLines` — shipped, §3.12–3.13 |
| 5 | `Users`, `Roles`, `UserRoles`, `AuditLogs`, `ApplicationSettings` |
| later | `SupplierReturns` and `SupplierReturnLines` (supplier returns and expiry write-offs, Milestone 4b in the roadmap); `Manufacturers` and `MedicineBarcodes` only if a pharmacy actually needs them |

Two departures from the plan's original list are deliberate and recorded in
[ADR 0005](decisions/0005-no-purchase-orders-or-goods-receipts.md): Milestone 2 ships **no**
`PurchaseHeaders`, `GoodsReceipts`, `GoodsReceiptLines`, `StockMovementReasons` or
`StockAdjustments` tables. The first three collapsed into `Purchases` and `PurchaseLines`, and the
last two are not needed while every adjustment already carries its own typed movement and reason
(§3.4).

A third departure is recorded in [ADR 0008](decisions/0008-sales-returns-and-stock-disposition.md):
there is no `SalesReturnLines.Disposition` lookup table, because a disposition is a value written into
historical records and must not be renumbered by inserting a row; the three values are a check
constraint instead (§3.13).

`InvoiceSequences` is implemented exactly on the rule stated here: a transactional counter row advanced
by one atomic statement (§3.11), never `MAX(Number) + 1`. Milestone 4 added the return tables listed
above and added the `RowVersion` to `Sales` before writing the first status change (§3.7).
