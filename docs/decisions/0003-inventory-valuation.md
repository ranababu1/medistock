# 0003 — Inventory valuation and costing method

- **Status:** Accepted
- **Date:** 2026-10-15
- **Deciders:** MediStock maintainers
- **Related:** [`EfDashboardQueries`](../../src/MediStock.Infrastructure/Dashboard/EfDashboardQueries.cs), [`MedicineBatch`](../../src/MediStock.Domain/Inventory/MedicineBatch.cs)

## Context

The plan lists "inventory valuation with a clearly documented costing method" as a Milestone 4
report, but the dashboard already needs a stock value on day one. Picking a method later would mean
either showing nothing now or showing a number whose meaning nobody wrote down — and a stock value
that nobody can explain is worse than no stock value, because it gets quoted in decisions.

The schema stores cost at the only place it is actually known: `MedicineBatches.PurchaseCost`, the
per-unit cost of that specific delivery. The candidate methods were:

| Method | Formula | Fit to this schema |
|---|---|---|
| Weighted average cost | `Σ qty × (Σ cost_i·qty_i / Σ qty_i)` | Needs a cost pool per medicine; the per-batch cost becomes decoration |
| FIFO | Cost of the *oldest* units still on hand | Requires layering and consumption bookkeeping that the ledger does not track |
| LIFO | Cost of the *newest* units | Not permitted under IFRS; no reason to want it |
| Specific identification ("actual cost") | `Σ qty_i × cost_i` per batch | Reads exactly the columns that exist |

Two secondary questions also needed an answer:

1. **What happens to non-sellable stock?** `MedicineBatch.WriteOff` deliberately keeps
   `QuantityOnHand` and only changes `Status`, because the physical goods still exist until they
   are destroyed. Expired and recalled batches therefore still hold quantity.
2. **Which batches count?** `QuantityOnHand = 0` rows are fully issued; `QuantityOnHand > 0` rows
   include sellable *and* non-sellable stock.

## Decision

**Specific identification.** Inventory value at cost is the sum, over every batch holding
quantity, of that batch's remaining quantity multiplied by that batch's own purchase cost:

```sql
SELECT SUM(QuantityOnHand * PurchaseCost)
FROM MedicineBatches
WHERE QuantityOnHand > 0;
```

Consequences of the choices embedded in that statement:

- **Non-sellable batches are included.** Expired, written-off and recalled stock is still owned
  stock. Excluding it would quietly understate inventory and let write-offs disappear from the
  books, which is exactly the kind of silent loss the plan warns against.
- **Fully issued batches are excluded** by `QuantityOnHand > 0`. A depleted row contributes zero
  anyway; the predicate keeps the estimate index-friendly.
- **No cost pool, no layers.** Nothing is averaged or re-sequenced, so the number is reproducible
  by hand from the batch table alone — the property the plan really wants from "clearly documented".
- **The number is a point-in-time cost of stock on hand, not a profit figure.** Gross margin needs
  sale-side costs, which arrive with point of sale in Milestone 3.

The dashboard surfaces this as `DashboardSnapshot.InventoryValueAtCost`. The name says what it is:
**at cost**, not at retail and not at market. Any UI that displays it must keep those words.

## Consequences

**Easier**

- The figure is auditable: sum the batch grid and you get the dashboard number. A pharmacist can
  check it against supplier invoices one line at a time.
- No hidden state. Adding a batch with a new cost cannot retroactively change the value of stock
  already on hand, which is what makes specific identification easy to explain.
- The same expression backs both the dashboard and the future valuation report, so they cannot
  disagree.

**Harder**

- Comparing two batches of the same medicine bought at different prices is the user's problem, not
  the system's. A "why did my stock value jump" question requires looking at the batch table.
- **The headline number mixes sellable and non-sellable stock.** Today the dashboard shows one
  figure; a pharmacist reading it as "what I can sell" would be wrong by the value of expired and
  written-off goods. This is a known, accepted gap with a named fix (below).
- No weighted average means no single "cost" per medicine, so future margin reports will need to
  compute cost of goods sold from the movements actually consumed rather than from a stored average.
- Valuation is only as correct as invariant I13 (cached balance equals ledger sum). If a balance
  ever drifted, valuation would drift with it.

**Expected of future code**

- **Never average costs across batches** without superseding this record. If weighted average is
  ever wanted, it is a new costing policy with its own column, not a reinterpretation of
  `PurchaseCost`.
- Any report that presents inventory value must state the costing method and the valuation date,
  and must distinguish sellable from non-sellable stock.
- Write-offs must keep changing `Status` and recording a `StockMovements` row. Zeroing
  `QuantityOnHand` to "remove" stock from the valuation would both break invariant I13 and hide a
  real loss.

## Follow-up work this creates

| Item | Milestone | Note |
|---|---|---|
| Split the dashboard figure into **sellable** and **non-sellable** value | 2 | **Done.** `DashboardSnapshot.SellableInventoryValueAtCost` and `NonSellableInventoryValueAtCost`, split with `ExpiryPolicy.IsSellable`, and shown under the total on the dashboard |
| Valuation report with a valuation-date parameter | 4 | Must reuse this exact expression, not re-derive it |
| Sell-through value of expired stock, to quantify waste | 4 | Useful before deciding disposal procedures |
| Cost of goods sold from consumed movements | 3 | Needed for gross margin; do not approximate it from the current figure |
