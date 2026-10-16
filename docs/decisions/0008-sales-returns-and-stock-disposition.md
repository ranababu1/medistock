# 0008 — Sales returns, refunds and the disposition of returned stock

- **Status:** Accepted — **disposition and refund rules need pharmacist and tax confirmation**
- **Date:** 2026-10-16
- **Deciders:** MediStock maintainers
[`EfReportQueries`](../../src/MediStock.Infrastructure/Reporting/EfReportQueries.cs), [`database-design.md` §3.12–3.13](../database-design.md), [ADR 0002](0002-expiry-date-interpretation.md), [ADR 0003](0003-inventory-valuation.md), [ADR 0006](0006-context-lifetime-and-execution-strategy.md), [ADR 0007](0007-point-of-sale-pricing-and-settlement.md)

## Context

The plan asks for returns that "record which original sale and line are being returned", "validate
returnable quantities", "record the reason, operator, time, and disposition of returned stock", insist
that returned medicine is **not** automatically put back into sellable stock, and correct mistakes
"through reversal or adjustment records, not by erasing history". It also requires a stock movement for
every "receipt, sale, return, adjustment, and write-off", forbids selling expired stock, and requires a
completed invoice to be preserved as a historical record.

Four things in that brief have more than one defensible answer, and each of them would silently corrupt
the numbers if it were answered by accident:

- **How much money comes back** when a line has already been partly returned, or when one line was drawn
  from two batches at different prices.
- **What a return does to stock** — whether the batch grows back, and what the ledger then says about
  medicine that is physically in the pharmacy but unsellable.
- **Whether a refund is a payment**, a negative invoice line, or a document of its own.
- **Rounding**: money is `decimal(18,2)` rounded at every pricing step (ADR 0007), so
  `refund = total × quantity ÷ soldQuantity` invents a paisa that was never charged, and repeating that
  arithmetic over several entries drifts further each time.

Two facts constrain the answer:

- An invoice is a **historical document**. What it printed must stay what it printed, because a reprint
  is evidence in a tax or drug-sale inspection.
- Stock moves only in the branch where money is taken, and the ledger must stay the single truth about
  what left the shelf (ADR 0003).

## Decision

**A return is its own numbered document that credits money against named invoice lines, records what
happened to the goods, and never rewrites the invoice.**

1. **`SalesReturns` and `SalesReturnLines`, not an edit of the sale.** A return carries its own number,
   business date, timestamp, operator, reason, refund method and notes; each of its lines names the
   `SaleLine` and the `MedicineBatch` it is credited against. Nothing on `Sales`, `SaleLines` or
   `Payments` is deleted, overwritten or re-signed.

2. **Returnable quantity is what is left of the allocation.** `SaleLineBatchAllocation.ReturnableQuantity`
   is the allocated quantity less everything already returned from that allocation, and
   `SaleLine.ReturnableQuantity` is the same at line level. Both requests are summed *within* one document
   before being checked, so a single return cannot smuggle two entries past the limit by asking for half
   of it twice.

3. **A return is refused before anything moves.** `SalesReturn.Record` makes two passes: the first
   validates every line (the invoice is not cancelled, the business date is not before the invoice's, the
   line exists, that line really did sell that batch, the batch loaded, the quantities are within the
   returnable amounts, and the disposition is allowed) and only then does the second pass apply anything.
   A basket with one bad entry returns nothing at all, because a half-applied return cannot be
   reconstructed by the operator afterwards.

4. **Money comes back proportionally, and the last paisa comes from the remainder.** Every money figure
   the line charged — total, tax and cost — is split across the returned quantity with
   `Money.ProportionalShare`, except the entry that clears the line, which receives
   `Money.RemainderShare` and so takes the exact residue. One expression,
   `Money.ShareOfLine(total, alreadyShared, quantity, lineQuantity, returnableQuantity)`, is used for all
   three figures, by `SalesReturn.Record`, by `SalesReturn.EstimateRefund` and by the counter's own refund
   preview. The consequence is testable and tested: a line returned in several entries refunds **exactly**
   what it charged, to the paisa, and its tax and cost likewise.

5. **Cost is credited from the line, not from the batch.** A return credits cost at the per-unit cost the
   line was costed at, so the credit and the charge it reverses are the same number and the margin report
   nets out to zero when everything comes back. Reading the batch's current purchase cost instead would
   make the credit differ from the charge whenever a batch is received again at a new price.

6. **Disposition is mandatory and per returned line.** Each `SalesReturnLine` is recorded as
   `Restock`, `Quarantine` or `Destroy` (stored as `1..3`, constrained by
   `CK_SalesReturnLines_Disposition_Valid`), with an optional note. There is no default: the operator
   decides per batch, and the decision is on the document.

7. **Only an explicit `Restock` touches stock, and only when the batch may take it back.**
   `MedicineBatch.RestoreQuantity` refuses a quantity for an expired, recalled or inactive batch, and
   `SalesReturn.Record` refuses to *plan* a `Restock` in that case and tells the operator to quarantine or
   destroy instead. "Not expired" is judged with `ExpiryPolicy.IsExpired` against the **business date of
   the return**, not the machine clock, so a return recorded for yesterday is judged as yesterday.

8. **`Quarantine` and `Destroy` write no stock movement, and that is deliberate.** The units left the
   shelf at the till and did not come back; the ledger keeps saying so, and the return document says why.
   Recording them as stock would put unsellable medicine into the valuation and into the FEFO candidate
   list. The trade-off is stated plainly: medicine that is quarantined is not visible anywhere as
   "owned but unsellable" — the expiry and valuation reports treat it as gone, which is the safe
   direction to be wrong in.

9. **A refund is never a `Payment`.** `CK_Payments_Amount_Positive` makes a refund inexpressible as a
   payment, and that is the point: `Payments` is money received against an invoice, while a refund is a
   credit against a historical sale with a reason and a disposition attached. The refund method is a
   field on the return (Cash, Card, UPI, Other), and a change at the counter is settled outside the
   system, as it already was at checkout.

10. **Cancelling an invoice is not a refund.** `Sale.Cancel` requires every line to be fully returned
    first, so the stock a cancellation reverses has already been put back or written off deliberately;
    it records the operator, the timestamp and a reason. A mistake is corrected by returning the sale and
    then cancelling it — never by deleting it. A cancelled invoice can take no further return.

11. **The return number comes from the same counter as the invoice number.** `RET-<yyyy>-000123`, allocated
    from `InvoiceSequences` by the same single `MERGE … WITH (HOLDLOCK) … OUTPUT` statement described in
    §3.11 of the database design, inside the return's transaction. Numbering cannot be separated from the
    write that consumes it, and two tills returning at the same instant cannot be handed the same
    document number. A return that fails to commit leaves a gap in the return series, which is
    acceptable; a duplicate would not be.

12. **Returned money accumulates; the invoice does not change.** `ReturnedQuantity`, `ReturnedAmount`,
    `ReturnedTax` and `ReturnedCost` on the line and the returned quantity on the allocation only ever
    grow, which is what makes the returns report, the net-of-returns takings and the double-refund check
    possible. A reprint of the original invoice still shows the figures it printed.

13. **A losing return is refused, not merged.** `Sales`, `SaleLines` and `SaleLineBatchAllocations` carry
    `rowversion` concurrency tokens. Two operators refunding the last unit of the same allocation
    concurrently cannot both commit: the loser matches no row, `DbUpdateConcurrencyException` is turned
    into an operator-facing refusal, and the shelf is credited once. This is the same conditional-write
    contract as ADR 0006, applied to returns.

14. **Reports read the same tables the counter writes, and show both sides of a return.** `IReportQueries`
    returns a `ReportTable` computed from `Sales`, `SalesReturns`, `Purchases`, `MedicineBatches` and
    `StockMovements`; sales figures are given gross and net of returns on the same row, so a return is
    never invisible and never double-counted. Ten definitions ship (daily sales, sales by medicine, sales
    by payment method, purchase history, stock by batch, low stock, expiring stock, stock movements,
    inventory valuation, sales returns). Ranges are normalised rather than trusted: a reversed range is an
    error, not an empty report, and the date axis is the local business date from `IClock`. CSV is the
    first export because it can be checked in a spreadsheet; PDF and Excel wait until the definitions and
    their totals have been reviewed.

15. **Restocking puts units back where they came from.** A restocked unit returns to its original batch,
    with its original expiry date and purchase cost, so FEFO continues to prefer the earliest expiry and
    the valuation stays specific to the batch that was actually bought. Nothing is merged into a new
    batch.

## Consequences

- A return is traceable in both directions: invoice → line → allocation → batch, and batch → what came
  back on it. Every figure on the return can be recomputed from the invoice it credits.
- The invariants I30–I39 in `database-design.md` §4 are the machine-checkable half of this decision, and
  `tests\MediStock.IntegrationTests\Sales\SaleReturnCatalogTests.cs` is the workflow half: a part return,
  the same units returned twice, an expired batch that may not be restocked, quarantine that leaves the
  ledger alone, a return against a cancelled invoice, two tills racing to refund the same unit, and the
  number sequence.
- `tests\MediStock.Domain.Tests\Sales\SalesReturnTests.cs` proves the arithmetic with no database: the
  preview equals the recorded refund, and a line split across several entries returns exactly its total.

**Known gaps, deliberately left open:** one refund method per return document (a return paid partly in
cash and partly by card needs two documents); no exchange workflow that returns a line and sells its
replacement in one transaction; no credit-note print-out to give the customer; supplier returns and
unassisted expiry write-offs are still modelled only as movement types, with no screen
(see `roadmap.md`, Milestone 4b); and the refund is not reconciled against a till or a card settlement
batch, because there is no settlement model yet (Milestone 5).

**This is not legal or clinical advice.** Whether returned medicine may be resold at all, what
constitutes a quarantine location, how long a return must be retained, and what a return or credit
document must say for tax purposes are questions for a pharmacist, a drug-licensing professional and a
tax professional in the jurisdiction of use. The dispositions implemented here are a software
structure, not a claim of compliance; confirm them, and the refund rules in ADR 0007, before the
application is used on real medicine.
