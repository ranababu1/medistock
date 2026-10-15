namespace MediStock.Application.Common;

/// <summary>
/// Supplies the current time so that business-date rules are testable and the local business date
/// is resolved in exactly one place. Daily reports group by this date, not by UTC.
/// </summary>
public interface IClock
{
    /// <summary>The current instant in UTC.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Today's date in the pharmacy's configured time zone.</summary>
    DateOnly BusinessDate { get; }
}
