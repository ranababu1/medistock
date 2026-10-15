using System.Globalization;
using MediStock.Application.Common;
using MediStock.Domain.Sales;
using MediStock.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MediStock.Infrastructure.Sales;

/// <summary>
/// Issues invoice numbers from the <c>InvoiceSequences</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The number is taken with a single <c>MERGE ... WITH (HOLDLOCK)</c> statement that inserts the
/// period's row if it is missing and advances it if it is not, returning the new value. Two tills
/// checking out at the same instant therefore cannot both be handed the same number: the first holds an
/// exclusive lock on the range until its transaction ends, and the second sees the advanced value. A
/// read-then-write would not have that property, which is why there is no "fetch the next number and
/// save it" code path anywhere in this class.
/// </para>
/// <para>
/// The statement must run inside the checkout transaction, so a sale that fails to commit releases its
/// number rather than burning it. Numbers are never <em>reissued</em>: a number only reaches a customer
/// through a committed sale. A cancelled sale later leaves a gap, and gaps are acceptable.
/// </para>
/// <para>
/// The unique index on <c>Sales.InvoiceNumber</c> is the second line of defence: even if this statement
/// were bypassed, the database would refuse to store two invoices under one number.
/// </para>
/// </remarks>
internal sealed class EfInvoiceNumberAllocator
{
    private const string AllocateSql = """
        MERGE INTO [dbo].[InvoiceSequences] WITH (HOLDLOCK) AS [target]
        USING (SELECT @key AS [SequenceKey]) AS [source]
            ON [target].[SequenceKey] = [source].[SequenceKey]
        WHEN MATCHED THEN
            UPDATE SET [NextNumber] = [target].[NextNumber] + 1, [UpdatedAtUtc] = @now
        WHEN NOT MATCHED THEN
            INSERT ([SequenceKey], [NextNumber], [CreatedAtUtc], [UpdatedAtUtc])
            VALUES (@key, 1, @now, @now)
        OUTPUT INSERTED.[NextNumber];
        """;

    private readonly IClock _clock;

    public EfInvoiceNumberAllocator(IClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Consumes and formats the next invoice number for the given business date. Callers must already be
    /// inside an open transaction: that is what makes the number theirs alone until it commits.
    /// </summary>
    public async Task<string> AllocateAsync(
        MediStockDbContext db,
        DateOnly businessDate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var key = InvoiceSequence.KeyFor(businessDate);

        // The transaction normally has the connection open already; opening it is idempotent and keeps
        // this method honest if it is ever the first to touch the database.
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = AllocateSql;
        command.Parameters.Add(new SqlParameter("@key", key));
        command.Parameters.Add(new SqlParameter("@now", _clock.UtcNow));
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (scalar is not null and not DBNull)
        {
            return InvoiceSequence.Format(key, Convert.ToInt64(scalar, CultureInfo.InvariantCulture));
        }

        throw new InvalidOperationException($"Allocating the invoice number for {key} returned no value.");
    }

    /// <summary>
    /// The number the next sale on that date would receive, without consuming it. Read-only, and
    /// therefore advisory only: another till may take it first.
    /// </summary>
    public async Task<string> PreviewAsync(
        MediStockDbContext db,
        DateOnly businessDate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var key = InvoiceSequence.KeyFor(businessDate);

        var current = await db.InvoiceSequences
            .AsNoTracking()
            .Where(sequence => sequence.SequenceKey == key)
            .Select(sequence => (long?)sequence.NextNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return InvoiceSequence.Format(key, (current ?? 0L) + 1L);
    }
}
