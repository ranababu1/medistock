# Architecture decision records

Numbered, immutable records of decisions that are expensive to reverse. Read
[0001](0001-record-architecture-decisions.md) first — it explains why these exist and how to add one.

| # | Title | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-expiry-date-interpretation.md) | Expiry date interpretation | Accepted — **needs pharmacist confirmation** |
| [0003](0003-inventory-valuation.md) | Inventory valuation and costing method | Accepted |
| [0004](0004-localdb-for-development.md) | LocalDB as the development and test database | Accepted — **deployment target unconfirmed** |
| [0005](0005-no-purchase-orders-or-goods-receipts.md) | One purchase document: no purchase orders, goods receipts or partial receiving | Accepted |
| [0006](0006-context-lifetime-and-execution-strategy.md) | One database context per operation, the execution-strategy contract, and conditional lifecycle writes | Accepted |

## Adding a record

1. Copy the shape of an existing record: title, status, date, deciders, then Context / Decision /
   Consequences.
2. Number it with the next free four-digit number. Do not renumber or delete existing records.
3. If you are changing your mind, write a new record and mark the old one
   `Superseded by NNNN`.
4. Link the record from any code whose correctness depends on it, using an XML doc comment:

   ```csharp
   /// <remarks>See <c>docs/decisions/0002-expiry-date-interpretation.md</c>.</remarks>
   ```
