namespace MediStock.IntegrationTests;

/// <summary>
/// The pharmacy's local business date, derived exactly the way <c>IClock.BusinessDate</c> derives it.
/// </summary>
/// <remarks>
/// Tests must never use <see cref="DateTime.UtcNow"/> for a business date: in a positive-offset time
/// zone such as India Standard Time the local date rolls over in the evening, and a UTC-derived date
/// silently disagrees with every date the application writes.
/// </remarks>
internal static class BusinessTime
{
    /// <summary>Evaluated on each access so a test run that crosses midnight stays consistent.</summary>
    internal static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local).DateTime);
}
