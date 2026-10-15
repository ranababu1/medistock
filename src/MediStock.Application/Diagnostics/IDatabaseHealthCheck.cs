namespace MediStock.Application.Diagnostics;

/// <summary>Result of probing the database, surfaced on the dashboard status bar.</summary>
public sealed record DatabaseHealthReport(
    bool IsReachable,
    string Provider,
    string? DatabaseName,
    string? ServerVersion,
    IReadOnlyList<string> PendingMigrations,
    TimeSpan Elapsed,
    string? Error)
{
    public bool HasPendingMigrations => PendingMigrations.Count > 0;

    public static DatabaseHealthReport Unreachable(string provider, string error, TimeSpan elapsed)
        => new(false, provider, null, null, [], elapsed, error);
}

public interface IDatabaseHealthCheck
{
    Task<DatabaseHealthReport> CheckAsync(CancellationToken cancellationToken = default);

    Task<int> ApplyPendingMigrationsAsync(CancellationToken cancellationToken = default);
}
