# 0005 — One purchase document, no purchase orders, goods receipts or partial receiving

- **Status:** Accepted
- **Date:** 2026-10-15
- **Deciders:** MediStock maintainers
- **Related:** [`Purchase`](../../src/MediStock.Domain/Purchasing/Purchase.cs), [`EfPurchaseCatalog`](../../src/MediStock.Infrastructure/Purchasing/EfPurchaseCatalog.cs), [`database-design.md` §3.5](../database-design.md)

## Context

The plan's conceptual data model (§4) lists `PurchaseHeaders`, `PurchaseLines`, `GoodsReceipts`,
`GoodsReceiptLines`, `SupplierReturns`/`SupplierReturnLines`, `StockAdjustments` and
`StockMovementReasons`, and asks for "support partial receiving **if included in the agreed scope**".
It also asks, in the same section, to "avoid adding abstractions without a clear use case".

In a single-pharmacy deployment one person buys and the same person receives. A purchase order that
is never sent anywhere, followed by a goods receipt that restates it, adds two tables, two state
machines and a reconciliation problem — "ordered 100, received 60, where are the other 40?" — to
model something the operator already writes on the supplier's invoice. Meanwhile the parts that
genuinely matter for correctness, traceability and reconciliation are unaffected by splitting the
document: one receipt movement per line, one batch per line, a lifecycle that cannot be replayed.

Partial receiving is the same question. Supporting it needs a per-line received quantity, a header
that is neither `Draft` nor fully `Received` while deliveries trickle in, and rules for what happens
when a partial line is cancelled. That is real complexity, and it is only worth paying for once a
pharmacy is actually receiving against orders.

## Decision

**A purchase is one document with one lifecycle, and receiving is all-or-nothing.**

1. **No purchase order entity.** `Purchase` covers both what was ordered and what arrived. The
   supplier's invoice number and date are the external identity; there is no internal document
   number to reconcile against.
2. **No goods receipt entity.** `Purchase.Receive(receivedOn, receivedBy)` is the receipt.
   `ReceivedOn`, `ReceivedAtUtc` and `ReceivedBy` on the header date and attribute it, and the
   batches and stock movements created in the same transaction are the receipt lines.
3. **No partial receiving.** `Receive` moves the whole purchase from `Draft` to `Received` in one
   transaction. `PurchaseLine.QuantityReceived` already exists so that partial receiving becomes a
   behaviour change rather than a schema change — see §3.6 of the database design.
4. **No `StockMovementReasons` lookup table.** Reasons are free text with a 300-character cap,
   mandatory for the movement types that need one
   (`ReasonsRequiredFor` in `StockMovementType`). A lookup table cannot express "broken in transit"
   or "customer returned the wrong box", and enforcing referential integrity on prose buys nothing.
5. **No `StockAdjustments` table.** An adjustment is a `StockMovement` of type `AdjustmentIncrease`
   or `AdjustmentDecrease` with a mandatory reason. A parallel table would be a second ledger to keep
   in step with the first.
6. **No `SupplierReturns` in Milestone 2.** Deferred to Milestone 4 with its own record; the
   `SupplierReturn` movement type already exists so nothing blocks it.

Corrections follow the same rule as everywhere else in MediStock: a received purchase is **reversed**
(`Status = Reversed`, reason and operator mandatory, matching `ReceiptReversal` movements) or
adjusted — never deleted. `IPurchaseCatalog` deliberately exposes no delete operation at all.

## Consequences

**Easier**

- The whole of purchasing is two tables and one aggregate, so the receiving screen shows one document
  with one status instead of a three-way match between order, receipt and invoice.
- The acceptance criterion "receiving stock creates the correct batches and movements" is verifiable
  by looking at a single purchase: N lines in, N batches and N receipt movements out.
- "Repeated submission cannot receive the same invoice twice" falls out of the lifecycle rather than
  from an idempotency key: `Receive` refuses a purchase that is not a `Draft`.
- Reversal is a first-class, auditable operation with no schema of its own.

**Harder**

- A delivery that matches its invoice only in part has to be received as one purchase and corrected
  afterwards, or split across two purchases. Neither is as convenient as partial receiving.
- There is no record of what was ordered but not yet delivered, so MediStock cannot answer "what is
  still outstanding with this supplier?" until purchase orders exist.
- A free-text reason cannot be counted or filtered by category, so "how often do we write off
  broken stock?" is a text search rather than a `GROUP BY`.
- `PurchaseLine.QuantityReceived` will sit at either `0` or `Quantity` and will look redundant to a
  future reader; §3.6 of the database design says why it is there.

**Expected of future code**

- Do not add a purchase order or goods receipt table without superseding this record. The reason to
  revisit is a pharmacy that genuinely receives against an order — not a UI preference.
- If partial receiving is implemented, `PurchaseLine.QuantityReceived` becomes the source of truth for
  what arrived, and `Purchase.Receive` must stop being a single all-or-nothing transition. The
  lifecycle check constraint is what will have to change with it.
- Any new correction path must add a typed `StockMovement`, not mutate a batch balance directly.
  The `ReceiptReversal` movement exists precisely so a reversal keeps invariant I13 (§4) intact.
