using System.Globalization;
using MediStock.Domain.Common;

namespace MediStock.Domain.Sales;

/// <summary>
/// The counter behind invoice numbers. One row per numbering period, holding the last number issued.
/// </summary>
/// <remarks>
/// <para>
/// Invoice numbers must be unique and must never be reused, and they are handed out while other tills
/// may be doing exactly the same thing. The allocation is therefore a single atomic statement against
/// this table inside the checkout transaction (see <c>EfInvoiceNumberAllocator</c>), not a read
/// followed by a write, which two tills could interleave into the same number.
/// </para>
/// <para>
/// The numbering period is the calendar year carried in the key, so the first invoice of 2027 is
/// <c>INV-2027-000001</c>. Numbers are never reissued, so a cancelled sale leaves a gap. Gaps are
/// acceptable; duplicates are not.
/// </para>
/// </remarks>
public sealed class InvoiceSequence
{
    public const int SequenceKeyMaxLength = 32;

    /// <summary>Digits in the numeric part of an invoice number, zero-padded.</summary>
    public const int NumberDigits = 6;

    /// <summary>Required by Entity Framework Core.</summary>
    private InvoiceSequence()
    {
    }

    /// <summary>Numbering period identifier, for example <c>INV-2026</c>. The primary key.</summary>
    public string SequenceKey { get; private set; } = string.Empty;

    /// <summary>The last number issued in this period. The next invoice uses this value plus one.</summary>
    public long NextNumber { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>The numbering period a business date belongs to.</summary>
    public static string KeyFor(DateOnly businessDate) =>
        string.Create(CultureInfo.InvariantCulture, $"INV-{businessDate.Year:D4}");

    public static InvoiceSequence Start(string sequenceKey, DateTimeOffset nowUtc)
    {
        var key = Guard.Required(sequenceKey, SequenceKeyMaxLength, "Sequence key");
        var timestamp = nowUtc;

        return new InvoiceSequence
        {
            SequenceKey = key,
            NextNumber = 0,
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp,
        };
    }

    /// <summary>
    /// Formats an issued number. Kept next to the key format so the two halves of the numbering scheme
    /// cannot drift apart.
    /// </summary>
    public static string Format(string sequenceKey, long number)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceKey);

        if (number <= 0)
        {
            throw new DomainValidationException("An invoice number must be positive.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sequenceKey}-{number.ToString($"D{NumberDigits}", CultureInfo.InvariantCulture)}");
    }
}
