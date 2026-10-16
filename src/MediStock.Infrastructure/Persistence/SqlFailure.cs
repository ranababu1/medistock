using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MediStock.Infrastructure.Persistence;

/// <summary>The kinds of refusal a SQL Server write can come back with that an operator can act on.</summary>
internal enum SqlFailureKind
{
    /// <summary>A unique index or primary key was violated: the row already exists.</summary>
    Duplicate,

    /// <summary>A check, foreign key or other constraint rejected the write.</summary>
    ConstraintViolation,
}

/// <summary>
/// Turns the <see cref="DbUpdateException"/> a save can raise into something the operator can be told
/// about, without either catalog having to know SQL Server error numbers.
/// </summary>
/// <remarks>
/// The numbers are stable and documented, so they live here once rather than in every catalog that
/// writes. The messages are not: a duplicate invoice number and a duplicate return number need
/// different words, so callers map the kind themselves.
/// </remarks>
internal static class SqlFailure
{
    /// <summary>A unique index was violated.</summary>
    private const int DuplicateKeyRow = 2601;

    /// <summary>A unique constraint was violated.</summary>
    private const int DuplicateKeyIndex = 2627;

    /// <summary>A check or referential constraint rejected the write.</summary>
    private const int ConstraintViolation = 547;

    public static bool TryClassify(
        DbUpdateException exception,
        out SqlFailureKind kind,
        out SqlException? sql)
    {
        ArgumentNullException.ThrowIfNull(exception);

        kind = default;
        sql = exception.InnerException as SqlException;

        if (sql is null)
        {
            return false;
        }

        switch (sql.Number)
        {
            case DuplicateKeyRow:
            case DuplicateKeyIndex:
                kind = SqlFailureKind.Duplicate;
                return true;

            case ConstraintViolation:
                kind = SqlFailureKind.ConstraintViolation;
                return true;

            default:
                return false;
        }
    }

    /// <summary>True when the server named one of the given constraints, indexes or tables.</summary>
    public static bool NamesAtLeastOne(SqlException? sql, params string[] names)
    {
        if (sql is null)
        {
            return false;
        }

        foreach (var name in names)
        {
            if (sql.Message.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
