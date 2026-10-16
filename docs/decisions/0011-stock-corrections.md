# 0011 — Stock corrections: adjustments, write-offs, recall quarantine and supplier returns

- **Status:** Accepted
- **Date:** 2026-10-16
- **Deciders:** MediStock maintainers
- **Related:** [`MedicineBatch`](../../src/MediStock.Domain/Inventory/MedicineBatch.cs), [`StockMovement`](../../src/MediStock.Domain/Inventory/StockMovement.cs), [`SupplierReturn`](../../src/MediStock.Domain/Suppliers/SupplierReturn.cs), [ADR 0005](0005-no-purchase-orders-or-goods-receipts.md), [ADR 0008](0008-sales-returns-and-stock-disposition.md), [ADR 0009](0009-users-roles-and-the-audit-trail.md)

## Context

Milestone 4 left three stock paths that a pharmacy needs but the application could
not yet express. Stock sometimes disagrees with the ledger because a count was
wrong or a pack broke. Expired and recalled batches stop being sellable but their
quantity still sits on the ledger, so the shelf and the reports disagree until
someone disposes of the goods. And medicine sometimes has to go back to the
supplier — expired stock for credit, overstocked lines — which today could only
be faked through a manual adjustment.

ADR 0005 already set the frame: an adjustment is a `StockMovement` with a
mandatory reason, not a parallel table, and supplier returns were deferred
until they got "their own record". The domain already carries the vocabulary —
`StockMovementType` has had `AdjustmentIncrease`, `AdjustmentDecrease`,
`ExpiryWriteOff`, `RecallWriteOff` and `SupplierReturn` values, and
`BatchStatus` has had `Expired`, `WrittenOff` and `Recalled`, since the first
inventory migration — but no workflow could reach them.

Two states of an un-sellable batch are possible and they mean different things:

- **Marked** (`WriteOff(reason, status)`, which exists today): the batch is
  blocked from all movement and carries a recorded reason, but its quantity
  still sits on the shelf and the ledger. This is quarantine: the goods are
  physically present, pending a decision or a recall courier.
- **Disposed**: the goods leave. The cached balance must fall and a movement
  must record the fall, or reconciliation — cached balance equals the sum of
  the ledger — breaks.

Nothing could compose a disposal: `WriteOff` refuses a batch with no quantity
left, and `DecreaseQuantity` refuses a batch already blocked, so a marked batch
could never be emptied, and an expired batch emptied by `DecreaseQuantity`
became `Depleted` and could then not be marked at all.

## Decision

**Disposal is one new domain operation; adjustments and supplier returns are
new workflows over operations that already existed. Every one of them is one
transaction that moves the cached balance, appends to the stock ledger, and
writes an audit entry.**

1. **Manual adjustments reuse `IncreaseQuantity`/`DecreaseQuantity`.** No new
   domain method. The workflow records an `AdjustmentIncrease` or
   `AdjustmentDecrease` movement with a mandatory reason and a free-text
   reference. A gain bumps `QuantityReceived` too — deliberate, unchanged
   behaviour: goods the supplier did not send make the receipt irreversible, and
   an increase on a `Depleted` batch reactivates it.
2. **Disposal is `MedicineBatch.WriteOffRemainingStock(reason, status)`.** It
   zeroes `QuantityOnHand` in the same step as it sets `Status` and
   `StatusReason`, and returns the quantity it disposed so the caller records
   exactly one movement for it. `status` is `WrittenOff` (expiry or damage
   disposal) or `Recalled` (recall disposal). The workflow picks the movement
   type from the outcome: `ExpiryWriteOff` for `WrittenOff`, `RecallWriteOff`
   for `Recalled`. Disposal always removes the **whole remaining quantity** —
   a partial loss is an adjustment, not a write-off.
3. **Quarantine stays a status, not a movement.** `WriteOff(reason, Recalled)`
   and `WriteOff(reason, WrittenOff)` keep their meaning: block the batch,
   record why, leave the quantity where it is. Disposal later moves the
   quantity out. Nothing is restockable while blocked, unchanged from
   `RestockBlocker`'s rules.
4. **A supplier return is a numbered document with its own tables**, as ADR 0005
   promised. `SupplierReturns`/`SupplierReturnLines` reference the supplier and
   the batches, carry the operator, reason, optional notes and the supplier's
   credit note number, and are numbered `SRET-YYYY-NNNNNN` from the same
   `InvoiceSequences` machinery as invoices and customer returns. Each line
   removes quantity through `DecreaseQuantity`, so an `Active` or `Expired`
   batch can go back (the classic expired-goods credit return) while a
   `Recalled` or `WrittenOff` batch is refused with the reason it was blocked —
   recalled stock follows the recall notice, not the credit-note pad.
   Reversal is deliberately absent: a supplier return that went out in error is
   corrected by receiving the goods again, and the correction is auditable in
   the movement history.
5. **Two new permissions, named after the activity.** `AdjustStock` covers
   manual adjustments and write-offs — one permission, because both say "the
   shelf is wrong without a sale or a purchase saying so". `ReturnToSupplier`
   covers supplier returns. The pharmacist and the inventory operator carry
   `AdjustStock`; the inventory operator and the administrator carry
   `ReturnToSupplier`. Three new audit actions name the events:
   `StockAdjusted`, `StockWrittenOff`, `SupplierReturnRecorded`.
6. **Quarantined stock is a read model over what already exists.** Three
   sources, one list: customer-return lines held in
   `ReturnDisposition.Quarantine` (never entered the ledger), batches
   `Recalled` with quantity still on hand, and batches `WrittenOff` by status
   with quantity still on hand. Expired stock with quantity already has its
   view; duplication would only invite disagreement.

## Consequences

**Easier**

- The milestone needed no schema change for adjustments or write-offs — the
  movement types, statuses and reasons were designed for this in Milestone 2 —
  so the risky part of the feature is one migration for two supplier-return
  tables and everything else is workflow over proven invariants.
- "Never sell expired, recalled, inactive or unavailable stock" needs no new
  rule: disposal, quarantine and supplier return all pass through state the
  sellable checks already refuse.
- Reconciliation keeps its single rule — cached balance equals ledger sum —
  because every quantity change, in every new path, appends to the ledger in
  the same transaction that changes the cache.

**Harder**

- `WriteOffRemainingStock` is the first domain operation that changes a
  quantity and a status together; its tests must pin that a disposal from each
  starting status lands on the right status and zeroes the balance in one
  call.
- The quarantine list is derived, not stored, so a future change to
  dispositions must keep the read model's filters in step.
- Operators must now choose between an adjustment and a write-off for damaged
  stock. The rule is stated on screen: part of a batch is an adjustment, all of
  it disposed forever is a write-off.