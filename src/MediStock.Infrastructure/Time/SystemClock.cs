using MediStock.Application.Common;
using MediStock.Application.Configuration;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Time;

/// <summary>
/// Resolves the pharmacy's local business date from the configured time zone. Deriving it here, in
/// one place, keeps daily reports consistent regardless of where the PC's clock is set.
/// </summary>
internal sealed class SystemClock : IClock
{
    private readonly TimeZoneInfo _timeZone;

    public SystemClock(MediStockOptions options, ILogger<SystemClock> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _timeZone = ResolveTimeZone(options.TimeZoneId, logger);
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateOnly BusinessDate => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(UtcNow, _timeZone).DateTime);

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Falling back keeps the app usable rather than failing to start over configuration.
            logger.LogWarning(
                ex,
                "Time zone '{TimeZoneId}' was not found on this machine. Falling back to the local time zone '{LocalZone}'.",
                timeZoneId,
                TimeZoneInfo.Local.Id);

            return TimeZoneInfo.Local;
        }
    }
}
