using System.Data;
using System.Data.Common;
using System.Diagnostics;
using MediStock.Application.Common;
using MediStock.Application.Configuration;
using MediStock.Application.Diagnostics;
using MediStock.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Diagnostics;

/// <summary>
/// Probes the SQL Server database for the shell's status indicator and applies migrations.
/// Error text is deliberately scrubbed of anything that could include credentials.
/// </summary>
internal sealed class SqlServerDatabaseHealthCheck : IDatabaseHealthCheck
{
    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly MediStockOptions _options;
    private readonly ILogger<SqlServerDatabaseHealthCheck> _logger;

    public SqlServerDatabaseHealthCheck(
        IDbContextFactory<MediStockDbContext> factory,
        MediStockOptions options,
        ILogger<SqlServerDatabaseHealthCheck> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static string GetProviderName(MediStockDbContext db) => db.Database.ProviderName ?? "Unknown";

    public async Task<DatabaseHealthReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var provider = "Unknown";

        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            provider = GetProviderName(db);

            var creator = db.Database.GetService<IRelationalDatabaseCreator>();
            if (!await creator.ExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                return new DatabaseHealthReport(
                    false,
                    provider,
                    null,
                    null,
                    [],
                    stopwatch.Elapsed,
                    $"The database '{db.Database.GetDbConnection().Database}' has not been created yet.");
            }

            // ExistsAsync opens and closes the connection, so it has to be reopened before the
            // connection-level metadata below can be read.
            var connection = db.Database.GetDbConnection();
            var opened = await EnsureOpenAsync(connection, cancellationToken).ConfigureAwait(false);

            try
            {
                var databaseName = connection.Database;
                var serverVersion = connection.ServerVersion;

                var pending = (await db.Database
                        .GetPendingMigrationsAsync(cancellationToken)
                        .ConfigureAwait(false))
                    .ToList();

                return new DatabaseHealthReport(
                    true,
                    provider,
                    databaseName,
                    serverVersion,
                    pending,
                    stopwatch.Elapsed,
                    null);
            }
            finally
            {
                if (opened)
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed.");
            return DatabaseHealthReport.Unreachable(provider, Describe(ex), stopwatch.Elapsed);
        }
    }

    /// <summary>Opens the connection when needed and reports whether this call opened it.</summary>
    private static async Task<bool> EnsureOpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State == ConnectionState.Open)
        {
            return false;
        }

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<int> ApplyPendingMigrationsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var pending = (await db.Database
                .GetPendingMigrationsAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToList();

        if (pending.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation(
            "Applying {Count} pending database migration(s): {Migrations}.",
            pending.Count,
            string.Join(", ", pending));

        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Database schema is up to date.");

        return pending.Count;
    }

    /// <summary>
    /// Produces an operator-friendly reason without echoing connection strings. SQL Server's own
    /// messages are already credential-free for integrated security, but the raw text is trimmed
    /// to its first line to avoid dumping stack detail into the UI.
    /// </summary>
    private static string Describe(Exception exception)
    {
        var text = exception switch
        {
            SqlException sql => sql.Message,
            InvalidOperationException invalid => invalid.Message,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return "The database could not be reached. Check that SQL Server is running and the connection string is correct.";
        }

        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? text;

        return firstLine.Length > 300 ? firstLine[..300] : firstLine;
    }
}
