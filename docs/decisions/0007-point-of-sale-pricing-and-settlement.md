# 0007 — Point-of-sale pricing, allocation, settlement and invoice numbering

- **Status:** Accepted — **pricing and tax rules need pharmacist and tax confirmation**
- **Date:** 2026-10-16
- **Deciders:** MediStock maintainers
- **Related:** [`SaleLine.Create`](../../src/MediStock.Domain/Sales/SaleLine.cs), [`Sale.Complete`](../../src/MediStock.Domain/Sales/Sale.cs), [`EfSaleCatalog`](../../src/MediStock.Infrastructure/Sales/EfSaleCatalog.cs), [`EfInvoiceNumberAllocator`](../../src/MediStock.Infrastructure/Sales/EfInvoiceNumberAllocator.cs), [`database-design.md` §3.7–3.11](../database-design.md), [ADR 0002](0002-expiry-date-interpretation.md), [ADR 0003](0003-inventory-valuation.md), [ADR 0006](0006-context-lifetime-and-execution-strategy.md)

## Context

The plan asks for a point of sale that "calculates discounts and taxes using explicit, tested rules",
"generates a unique invoice number", preserves completed invoices as historical records, and enforces
one transaction invariant: *"A completed sale, its sale lines, payment records, batch allocations, and
stock movements must succeed or fail together within a database transaction. Do not reduce stock before
the sale is safely committed."*

It does not say how price is chosen, in what order money is rounded, whether tax is inclusive or
exclusive, what a discount is a percentage *of*, whether change is part of an invoice, or how the
invoice number is generated. Every one of those is a decision that, if made silently, makes the
totals on a printed invoice unreproducible. The plan also insists that no clinical or financial rule
be presented as compliant before a professional has reviewed it, and that the first release prioritise
correctness and traceability over convenience.

Two facts constrain the answer:

- An invoice is a **historical document**. A tax rate, a catalogue name or a rounding policy may change
  after the sale. A reprint three years later still has to say what the customer actually paid.
- Stock moves only in the branch where money is taken. The ledger (ADR 0003 and the `StockMovements`
  table) must stay the single truth about what left the shelf, and it must never be reduced before the
  sale is safely committed.

## Decision

**One pricing pipeline prices both the quote and the checkout, and an invoice stores every figure it
printed.**

1. **`QuoteAsync` and `CheckoutAsync` share one code path.** Both call `EfSaleCatalog.BuildAsync`,
   which reads the medicine, reads the batches, applies the allocation policy and calls
   `SaleLine.Create`. The quote is a courtesy to the operator; the checkout is the authority. Because
   they share the code, what the operator was shown and what the customer is charged cannot drift —
   `A_quote_and_its_checkout_agree_to_the_paisa` asserts it.

2. **The order of calculation within a line is fixed and tested.** `round` means
   `Money.Round` — half away from zero, two decimal places, the retail convention.

   ```text
   gross    = round(quantity × unit price)
   discount = an absolute amount, never more than gross
   taxable  = gross − discount              (not rounded again)
   tax      = round(taxable × tax rate ÷ 100)
   total    = taxable + tax
   ```

   Each step is rounded once and the result is carried forward, so a line printed on its own adds up
   the same way it does on the invoice.

3. **Totals are the sum of the stored line values, not a rounding of an unrounded sum.**
   `SaleTotals.From` is the only place that computes them, and `Sale.Complete` writes its six figures
   onto the invoice. SQL Server enforces the result with `CK_Sales_Totals_Reconcile` and
   `CK_SaleLines_Amounts_Consistent`, so a database holding an invoice whose lines do not add up to
   its total is not possible.

4. **Every money column, and everything a reprint needs, is a snapshot.** `SaleLines` stores the
   medicine name, the unit price charged, the unit cost, the tax rate and each rounding result;
   `SaleLineBatchAllocations` stores the batch number and the expiry date as they were at the time of
   sale. Nothing on a stored invoice is recomputed from the catalogue, the batch row or the tax
   configuration.

5. **Price.** The operator may type a price on the line (`SaleLineDraft.UnitPrice`). If they do not, the
   line takes **the selling price of the first batch allocated**. A line that spans batches priced
   differently therefore takes the first batch's price, and the operator overrides it if that is
   wrong. This is a deliberate simplification: a proper multi-price line would need a price per
   allocation and a different printed layout.

6. **Cost.** `UnitCost` is the quantity-weighted average of the batches consumed, rounded once.
   `LineCost = round(quantity × UnitCost)` can therefore differ by one paisa from the true
   `Σ(quantity × batch cost)`. Margin reporting uses this figure and is accurate to the paisa per
   line. It is **not** the valuation figure: valuation is per-batch specific identification
   ([ADR 0003](0003-inventory-valuation.md)) and the two must never be conflated.

7. **Tax is one rate per line, defaulting from the catalogue.** `SaleLineDraft.TaxRatePercent` overrides
   `Medicine.TaxRatePercent` when present; both are constrained to 0–100. There is no tax engine here:
   no inclusive/exclusive flag, no HSN codes, no rounding to whole rupees, no exemption certificates.
   A single configured rate is what the first release can honestly claim.

8. **Discounts are absolute amounts on a line, and never exceed the gross.** No percentage discounts,
   no basket-level discount, no loyalty scheme. An absolute amount is what a counter actually
   applies, it reverses exactly, and it cannot exceed the line value — `SaleLine.Create` throws and a
   check constraint refuses it either way.

9. **Settlement must be exact.** `Sale.Complete` refuses an invoice whose payments do not sum to its
   total. `Payment.Amount` is the amount **settled**, not the amount tendered: change is a till
   concern, not an invoice line. Several payments may settle one sale, which is how a split payment is
   recorded. Negative amounts are impossible, so a refund is a *return document* (Milestone 4), never a
   negative payment on the original invoice.

10. **Allocation: an explicit request outranks the policy, then first-expiry-first-out.** Only batches
    that `ExpiryPolicy` calls sellable are candidates — active medicine, positive quantity, not
    expired as of the business date (ADR 0002). A blocked line reports *why* rather than throwing,
    because a half-finished basket is a normal state of the point-of-sale screen. The allocation must
    cover the quantity exactly: `SaleLine.Create` refuses a line whose allocations do not sum to its
    quantity.

11. **The whole basket is priced before anything is written, and lines within it cannot oversell a
    batch.** `BuildAsync` keeps a dictionary of promised quantities per batch, so a second line for the
    same medicine sees the stock the first line has already claimed. Only after every line has been
    priced and no blockers remain does the checkout take a *single* stock write per batch.

12. **Checkout writes in one order, inside one transaction, and reduces stock last.**

    ```text
    clear the change tracker → begin transaction → build the basket
      → stop if there are blockers
      → record the payments
      → take the invoice number
      → build the sale
      → decrease each batch
      → save (invoice first, so movements can quote its number)
      → write one Sale movement per batch, quoting the batch's resulting balance
      → save → commit
    ```

    A failure commits nothing: no invoice, no payment, no allocation, no movement, no reduced shelf.

13. **Invoice numbers come from one atomic statement, and gaps are acceptable.** `InvoiceSequences` has
    one row per numbering period (`INV-2026`, `INV-2027`, …) holding the last number issued;
    `EfInvoiceNumberAllocator` advances it with a single
    `MERGE … WITH (HOLDLOCK) … OUTPUT INSERTED.[NextNumber]` **inside the checkout transaction**, and
    the number is formatted as `INV-2026-000123`. Two tills checking out at the same instant cannot
    receive the same number, because the first holds an exclusive lock on the row until its
    transaction ends. There is deliberately **no read-then-write** path and **no concurrency token** on
    this row: it is never read before it is advanced, so a token would add a second failure mode
    without closing a race the statement already closes. A sale that fails to commit gives its number
    back, a later cancellation leaves a gap, and `UX_Sales_InvoiceNumber` is the second line of defence
    behind the statement.

14. **A checkout that loses a race is repeated, and only then refused.** The batch rowversion from
    [ADR 0006](0006-context-lifetime-and-execution-strategy.md) makes the quantity write conditional, so
    two tills selling the last unit cannot both succeed. The loser has committed nothing, so
    `EfSaleCatalog` repeats the entire unit of work — up to eight attempts with a short, growing,
    randomised pause — before telling the operator that the stock moved under them. A basket is
    therefore refused only when the stock really is short, not merely when another till was busy. Each
    abandoned attempt leaves its invoice number behind as a gap.

15. **The business date is taken from `IClock` at the moment of checkout, and a sale cannot be
    back-dated.** `SaleDraft` carries no date, so every sale counts towards the pharmacy's local day at
    the time it was rung up. Correcting a wrong day is a correction document (Milestone 4), not an edit.

16. **The receipt is a file; the invoice is the database row.** `TextReceiptExporter` writes a
    reprintable receipt under `MediStock:ReceiptFolder` (default
    `%LOCALAPPDATA%\MediStock\receipts`). Writing a receipt changes no business state. CSV, PDF and
    Excel report exports belong to Milestone 4, after the report definitions are tested.

17. **The stored order of allocations is expiry ascending, not pick order.** `Sale.Complete` keeps the
    order the allocator returned, but the DTO that loads a sale sorts allocations by expiry date and
    then by batch id, so a reprint reads as a shelf-life instruction. Which batch was picked *first* is
    visible in the shelf itself (the batches were decremented in pick order) and in the movements, not
    in the allocation ordering.

## Consequences

**Easier**

- Every acceptance criterion for the milestone is checkable against one stored invoice: the amounts
  reconcile, the batches are named, the movements carry the batch's resulting balance, and the
  payment settles the total to the paisa.
- A reprint is honest by construction. Changing a tax rate, renaming a medicine or re-pricing a batch
  cannot alter a single historic figure, because nothing is recomputed.
- The invariant "a failed sale leaves no partial sale, payment, or stock changes" is a property of the
  code shape — one transaction, one order, stock last — rather than something to remember to check.
- Invoice numbering has no gap-free requirement to defend, which removes the entire class of
  "renumber the day's invoices" work.

**Harder**

- A line spanning batches with different selling prices is priced at the *first* batch's price, so the
  pharmacy can under- or over-charge by the difference unless the operator overrides it. There is no
  per-allocation price and no warning.
- A percentage discount cannot be expressed, so a "10 % off" promotion is mental arithmetic at the
  counter.
- `LineCost` can differ by a paisa from the cost of the units actually consumed, so a margin report
  reconciled against per-batch cost will show a rounding difference.
- An abandoned checkout burns an invoice number, so the daily invoice sequence has holes. Any future
  report or auditor that assumes contiguous invoice numbers is wrong.
- Repeated attempts each re-read the whole basket, so a checkout that keeps losing a race does more
  database work than a single attempt would. Eight attempts is the current ceiling.
- Tax handling is a single rate per line. A pharmacy that needs inclusive pricing, multiple tax
  components or statutory rounding will find this inadequate, and the fix is a change here, not a
  configuration option.

**Expected of future code**

- Do not recompute a stored figure from live data, ever — not on a reprint, not in a report, not for
  "convenience". If a report needs a different view of history, store more at the time of sale.
- Any new write path that takes money or moves stock must reuse `BuildAsync` rather than re-deriving
  prices or allocations. A second pricing implementation is a second answer to what the customer owes.
- Milestone 4 returns must credit against `SaleLineBatchAllocations`, respect the returnable quantity
  (allocated minus already returned), take a *disposition* decision before anything returns to
  sellable stock, and be their own documents — never edits to the original invoice.
- Cancelling a sale must move `Status` to `Cancelled` and write the compensating stock movements; it
  must not delete the invoice, its lines, its allocations or its payments.
- If the pharmacy wants percentage discounts, inclusive tax or per-allocation pricing, supersede this
  record. Each of those changes what a printed invoice means.
- Before real use, a pharmacist must confirm the discount, tax and pricing rules, and a tax
  professional must confirm the invoice format and the tax treatment. Nothing here claims compliance.
