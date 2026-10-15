namespace MediStock.Infrastructure.Persistence.Configurations;

/// <summary>
/// Names of the shadow concurrency tokens mapped on the entities whose state must never be written
/// over a stale read.
/// </summary>
/// <remarks>
/// A token is a SQL Server <c>rowversion</c>: the database changes it on every update, EF Core appends
/// it to the <c>WHERE</c> clause, and a writer that read the row earlier matches nothing and is
/// refused instead of overwriting newer state. Mapped as shadow properties because concurrency is a
/// persistence concern; the domain aggregates have no reason to know about it. See
/// <c>docs/decisions/0006-context-lifetime-and-execution-strategy.md</c>.
/// </remarks>
internal static class ConcurrencyToken
{
    internal const string RowVersion = "RowVersion";
}
