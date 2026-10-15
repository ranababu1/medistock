# 0006 — One database context per operation, the execution-strategy contract, and conditional lifecycle writes

- **Status:** Accepted
- **Date:** 2026-10-16
- **Deciders:** MediStock maintainers
- **Related:** [`DependencyInjection.cs`](../../src/MediStock.Infrastructure/DependencyInjection.cs), [`EfPurchaseCatalog`](../../src/MediStock.Infrastructure/Purchasing/EfPurchaseCatalog.cs), [`PurchaseConfiguration`](../../src/MediStock.Infrastructure/Persistence/Configurations/PurchaseConfiguration.cs)

## Context

Milestone 2 added the first screens that load several lists at once, and the desktop application
started throwing `InvalidOperationException` from EF Core's `ConcurrencyDetector`:

> A second operation was started on this context instance before a previous operation completed.

The cause was the composition root, not the new screens. `AddDbContext` registers
`MediStockDbContext` as **scoped**, and every catalog was scoped with it. A WPF application has no
request boundary, so the whole process shares one scope: the sidebar, the dashboard and any screen
opened from it all resolved *the same* `DbContext` for the lifetime of the process. Two overlapping
`await`s on that one context are a genuine error, and the app happened to get away with it in
Milestone 1 only because the dashboard was the sole consumer.

A second, unrelated failure appeared in the same milestone. `ConfigureDatabase` configures
`EnableRetryOnFailure` so that transient network and failover faults do not lose a receipt. EF Core
refuses, by design, to execute a statement inside a **user-initiated** transaction under a retrying
execution strategy, because a retry must replay the *whole* unit of work rather than resume halfway
through:

> The configured execution strategy 'SqlServerRetryingExecutionStrategy' does not support
> user-initiated transactions. Use the execution strategy returned by
> 'DbContext.Database.CreateExecutionStrategy()' to execute all the operations in the transaction as
> a retryable unit.

That exception is thrown on the **first statement executed inside** the transaction, not by
`BeginTransactionAsync`, so it is easy to miss: `ReceiveAsync`, `UpdateAsync` and `ReverseAsync` all
opened a transaction and *then* ran their first query, which meant every transactional purchasing
operation in the product was broken. Integration tests caught it; a compile never would have.

## Decision

**1. A database context is created for one operation and disposed with it.**

`AddDbContextFactory<MediStockDbContext>` is registered instead of `AddDbContext`, and every catalog
and query class takes `IDbContextFactory<MediStockDbContext>` and opens its own context:

```csharp
public async Task<PurchaseDto?> FindAsync(int purchaseId, CancellationToken cancellationToken = default)
{
    await using var db = await _factory.CreateDbContextAsync(cancellationToken);
    …
}
```

A short-lived context cannot be shared, so overlapping work cannot collide on it, and the
`ChangeTracker` starts empty on every call — the caller never inherits another operation's pending
changes. The factory itself is a singleton, which is what makes it safe to inject into the
singletons and long-lived view models the desktop layer keeps alive.

`AddDbContextFactory` does **not** register `MediStockDbContext` itself, so
`DependencyInjection` keeps an explicit scoped registration that resolves a context from the same
factory. That registration is for the callers that genuinely own a context across several steps —
migrations, the development seeder, and the integration test fixture, which resolves
`MediStockDbContext` from a scope under `ValidateScopes = true`.

**2. Every user-initiated transaction runs inside the execution strategy.**

Any method that opens a transaction wraps the *entire* unit of work:

```csharp
var outcome = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
{
    // Every attempt repeats the whole unit of work, never part of one.
    db.ChangeTracker.Clear();

    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

    … queries, domain calls, SaveChanges …

    await transaction.CommitAsync(cancellationToken);
    return OperationResult<int>.Success(purchase.Id);
});
```

Three details are part of the decision, not style:

- **`ChangeTracker.Clear()` is the first statement of the attempt.** Without it a retry finds the
  entities the previous, failed attempt mutated still tracked and marked dirty, and reports a
  misleading domain error such as "Only a draft purchase can be received" — the aggregate is
  consulted in its already-modified state.
- **The lambda returns the affected purchase's identifier, not a DTO.** Reloading and logging happen
  *after* the strategy, so a retry can neither repeat the reload nor write the same success line
  twice.
- **Only the transactional methods are wrapped.** `SearchAsync`, `FindAsync`, `CreateAsync` and
  `FindByInvoiceAsync` are a single round trip and need no unit of work.

**3. The purchase header carries a database `rowversion`, so a lifecycle transition is a conditional write.**

The plan requires that two simultaneous receives of one invoice cannot both take the stock in. An
earlier version of this record argued that no version column was needed, on the grounds that the
winner's `UPDATE [Purchases]` holds an exclusive lock on the row until it commits, so the loser's
`SELECT` blocks and then re-reads the row as `Received`. **That argument is wrong, and a two-session
probe against LocalDB showed it.** Under READ COMMITTED the loser's `SELECT` is normally issued
*before* the winner's `UPDATE` — the two statements are milliseconds apart and a plain read does not
block — so the loser keeps a `Draft` copy. EF Core then emits an unconditional

```sql
UPDATE [Purchases] SET [Status] = @p WHERE [Id] = @p;
```

which matches the row whatever state it is in, so the loser's stale write lands after the winner has
committed. Both receives then take the same delivery into stock: two batches for one invoice line, two
`Receipt` movements, and the line repointed at whichever batch was written last. The same blind write
let a `Cancel` overwrite a committed `Received`, after which `CanReverse` refuses the reversal —
corruption that no screen in the application can repair. The race test passed only through scheduling
luck.

`Purchase` therefore maps a shadow `rowversion`:

```csharp
builder.Property<byte[]>(RowVersionProperty).IsRowVersion();
```

EF Core appends the token to every update of the header and reads the new value back, so the write
becomes `UPDATE [Purchases] SET … WHERE [Id] = @p AND [RowVersion] = @original`, and a stale writer
matches no row and is refused. Each unit of work in `EfPurchaseCatalog` runs through
`ExecuteUnitOfWorkAsync`, which converts `DbUpdateConcurrencyException` into a message the operator can
act on *("Another operator changed this purchase while you were working on it… Reload the purchase to
see its current state and try again.")* instead of a stack trace, and — because the exception is raised
inside the transaction and the code returns without committing — guarantees the loser wrote nothing.

The token is mapped as a **shadow property** because concurrency is a persistence concern and the
aggregate has no reason to know about it. A `byte[] RowVersion` property on `Purchase` works equally
well and is the more familiar EF Core idiom; the shadow mapping was chosen to keep the domain model
free of it.

Two alternatives were considered and rejected:

- **`SELECT … WITH (UPDLOCK)` on the purchase row at the start of the transaction.** It would serialise
the two operations, but it helps only if every future writer remembers the hint, it still needs a
conditional check to notice that the row moved underneath, and it puts the read into the locking
protocol instead of the write.
- **A unique index on `MedicineBatches(MedicineId, BatchNumber, ExpiryDate)`.** It would not stop the
double receive — the two racing batches share that key, so one would be rejected as a duplicate *after*
both had been accepted by the domain — and it would be wrong in its own right: a supplier may deliver
the same batch number with the same expiry on a later invoice, which the schema must allow.

Two integration tests hold the line: `A_stale_copy_of_a_received_purchase_cannot_be_written_back` is
deterministic and fails the moment the token stops being emitted, while
`Two_receives_racing_…` and `Two_reversals_racing_…` exercise the real race end to end. The racing tests
are opportunistic by nature — the loser may read after the winner has already committed and be refused
by the lifecycle guard instead of the token — so they are a backstop, not the proof.

## Consequences

**Easier**

- View models may issue overlapping async work — dashboard, sidebar status and a screen's first load
  — without sharing state.
- The retry policy can stay on, so a transient fault during receiving retries the whole receipt
  instead of failing it or leaving half a delivery on the shelf.
- `ChangeTracker.Clear()` at the top of each attempt is a single, visible statement of the retry
  contract; nothing depends on what a previous attempt left behind.
- Reconciliation is unaffected because a reversed receipt writes a `ReceiptReversal` movement rather
  than erasing the `Receipt` one.

**Harder**

- Querying a related entity in a second call and expecting identity resolution to still hold does
  not work: each call has its own change tracker. Callers that need one aggregate to span several
  steps must resolve `MediStockDbContext` from their own scope and pass it in — the seeder does
  exactly that.
- Two classes of bug are now possible that the old shared context hid: an operation that opens a
  context per call pays one extra connection round trip, and a caller that *does* share a context
  across two overlapping `await`s will still collide. The factory makes that a deliberate choice
  rather than the default.
- The execution strategy has to be remembered by hand. Forgetting it fails loudly — but only at
  runtime, on the first statement inside the transaction.
- A lifecycle transition can now fail because somebody else changed the purchase first, and that
  failure arrives as `DbUpdateConcurrencyException` rather than a domain error. Every path that mutates
  a purchase goes through `ExecuteUnitOfWorkAsync`, so the conversion happens in exactly one place; a
  new path that forgets it will surface the exception instead of a message.
- The token only guards the row it is on. `MedicineBatches.QuantityOnHand` is still updated
  unconditionally, so a future operation that moves batch stock outside a lifecycle transition —
  checkout, an adjustment — must bound its own write (`WHERE QuantityOnHand >= @quantity` with a
  row-count check) or take the same token.

**Expected of future code**

- A new catalog or query class takes `IDbContextFactory<MediStockDbContext>`, not
  `MediStockDbContext`, unless it is documented as owning a context across several steps.
- A new transaction is wrapped in `Database.CreateExecutionStrategy().ExecuteAsync(...)` from the
  first statement to the commit, with `ChangeTracker.Clear()` at the top of the attempt and no
  logging or reloading inside it.
- A new entity whose state change takes stock in or out of the shelf gets a `rowversion` and is written
  conditionally. This is the pattern to copy in Milestone 3: a blind `UPDATE … WHERE [Id] = @p` over a
  value that was read earlier is the defect, whatever the table.
- Do not remove `EnableRetryOnFailure` to make a transaction easier to write. That trades a
  compile-time-free convenience for the loss of automatic recovery from a dropped connection.
- If a future operation can fail after its transaction is committed but before the caller sees the
  result, revisit this record: the strategy makes the *work* repeatable, not the *response*.
