# 0002 — Expiry date interpretation

- **Status:** Accepted, **pending stakeholder confirmation**
- **Date:** 2026-10-15
- **Deciders:** MediStock maintainers
- **Related:** [`ExpiryPolicy`](../../src/MediStock.Domain/Inventory/ExpiryPolicy.cs)

## Context

The plan requires that MediStock "never sell expired, recalled, inactive, or unavailable stock"
and that it "define how expiry dates are interpreted, and validate this with pharmacy
stakeholders". That is harder than it looks, because the printed expiry on a medicine pack is
ambiguous:

- A pack may be printed `EXP 03/2027` (month and year only).
- A pack may be printed `EXP 2027-03` or `EXP 03/2027`.
- A pack may be printed with a full day, `EXP 2027-03-15`.
- Some manufacturers intend the printed date as the **first** day the product is no longer valid;
  others as the **last** day it is still valid. Practice varies by manufacturer and by market.
- Some regulations define the end of shelf life as the last day of the printed month.

Getting this wrong by one day is a real, if small, patient-safety and regulatory risk, and it is
the kind of off-by-one that hides easily: a batch is only wrong on one day out of hundreds, so it
will pass every casual test.

Three interpretations were considered.

| Option | Rule | Effect |
|---|---|---|
| A | `ExpiryDate` is the **last dispensable day** | Sellable while `businessDate <= ExpiryDate`; expired the next day |
| B | `ExpiryDate` is the **first expired day** | Sellable while `businessDate < ExpiryDate`; dead one day earlier than A |
| C | Store both an expiry date and a flag describing its meaning | Most faithful, most complex, and every query must branch |

There is also the question of what to store when the pack only prints a month. Storing the first of
the month with option A silently discards up to 30 days of usable shelf life; storing the last of
the month with option A is generous if the manufacturer meant option B.

## Decision

**Option A.** `ExpiryDate` is the **last day on which the batch may be dispensed**. A batch is
sellable while `businessDate <= ExpiryDate` and is treated as expired from the following day.

- `ExpiryPolicy.IsExpired(expiryDate, businessDate)` returns `businessDate > expiryDate`.
- `ExpiryPolicy.IsSellable(...)` requires `BatchStatus.Active`, `QuantityOnHand > 0` and
  `!IsExpired(...)`. Nothing else in the codebase is allowed to test expiry itself.
- `ExpiryPolicy.DaysUntilExpiry(...)` returns a value that is `0` on the last dispensable day and
  negative once expired.
- When only a month and year are printed, the **last day of that month** is stored. That matches
  the "last dispensable day" rule and is the conventional reading of a month-level expiry.
- Comparisons use the **business date** (`IClock.BusinessDate`, derived from
  `MediStock:TimeZoneId`), never UTC. A sale at 23:30 local time belongs to the local day.

The expiry date is stored as SQL `date` and reaches the database only after
`CK_MedicineBatches_ExpiryAfterReceipt` has confirmed it falls after `ReceivedOn`, so a
data-entry typo (`2027` instead of `2027-03`) is rejected rather than silently accepted.

## Consequences

**Easier**

- One rule, one class, one test file. Every report, dashboard and (future) point-of-sale check
  agrees by construction.
- Expiry is a pure function of `(expiryDate, businessDate)`, so it is trivially unit testable and
  needs no database.
- A pharmacist reading the near-expiry report sees a batch on its last valid day, not hidden a day
  early.

**Harder**

- If a future supplier or market turns out to mean option B, the fix is a one-line change in
  `ExpiryPolicy` **plus a data migration** for batches already stored under the old meaning. The
  data, not the code, is the expensive part.
- Month-only packs store a date the manufacturer may not literally have printed. Anyone auditing
  batch data must know the convention, which is why it is written here.

**Expected of future code**

- **No query, view model or SQL statement may compare `ExpiryDate` directly.** Route every expiry
  decision through `ExpiryPolicy` in C#, and use `ExpiryDate >= @businessDate` in SQL for the
  sellable-batch filter so the two stay equivalent.
- Near-expiry windows use `DaysUntilExpiry`, so a batch whose expiry is today has 0 days left and
  still appears in "expiring within N days" for any `N >= 0`.
- A write-off for expiry sets `BatchStatus.Expired` and records the automatic reason
  (`MedicineBatch.RefreshExpiryStatus`), never a free-text reason, so expiry write-offs remain
  countable.

## Open item

This interpretation is the maintainers' best reading of common pharmacy practice, **not** a
validated requirement. Before MediStock is used with real stock, a qualified pharmacy operator must
confirm:

1. Whether printed month-level expiries mean the last day of the month in this market.
2. Whether any supplier in use prints a "first expired day" style date.
3. Whether any applicable regulation overrides the above.

When that review happens, this record is either confirmed or superseded by a new one.
