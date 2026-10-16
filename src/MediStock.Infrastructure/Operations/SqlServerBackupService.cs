using System.Data;
using MediStock.Application.Common;
using MediStock.Application.Configuration;
using MediStock.Application.Identity;
using MediStock.Application.Operations;
using MediStock.Domain.Identity;
using MediStock.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Operations;

/// <summary>
/// Backup and restore over the SQL Server engine itself, rather than over Entity Framework Core.
/// </summary>
/// <remarks>
/// <para>
/// <c>BACKUP</c> and <c>RESTORE</c> cannot run inside a transaction and cannot be expressed in LINQ, so this
/// is the one place in the infrastructure layer that talks to SQL Server with a
/// <see cref="SqlConnection"/> and a command of its own. Everything it needs from the application — the
/// connection string, the folder, the clock, the trail — comes through interfaces like everywhere else.
/// </para>
/// <para>
/// The backup is <c>COPY_ONLY</c>: a manual backup taken on a whim must not reset the differential base of
/// an unrelated backup schedule. It is also <c>CHECKSUM</c>ed, so <see cref="VerifyAsync"/> has something
/// real to check and a backup written to a failing disk is discovered before it is needed.
/// </para>
/// <para>
/// Restoring takes the database to <c>SINGLE_USER</c> on a connection to <c>master</c>, because a restore
/// cannot proceed while any other connection is open. Pooled connections from this process are dropped
/// first: without that, the application's own idle contexts would keep the database busy and the restore
/// would wait until it timed out.
/// </para>
/// </remarks>
internal sealed class SqlServerBackupService : IBackupService
{
    /// <summary>How long a backup or restore may take before the connection gives up.</summary>
    private const int CommandTimeoutSeconds = 600;

    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly IUserSession _session;
    private readonly IAuditTrail _audit;
    private readonly IClock _clock;
    private readonly ILogger<SqlServerBackupService> _logger;

    public SqlServerBackupService(
        IDbContextFactory<MediStockDbContext> factory,
        IUserSession session,
        IAuditTrail audit,
        MediStockOptions options,
        IClock clock,
        ILogger<SqlServerBackupService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Folder = OutputFolder.Resolve(options.BackupFolder, @"MediStock\Backups");
    }

    public string Folder { get; }

    public Task<IReadOnlyList<BackupFile>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Listing is a read, and it is a read of administrative information — when backups were taken, and how
        // big they are. Everything else on this screen is refused to an operator without the permission, so
        // this is too rather than leaving the one path that answers a lesser role.
        _session.Require(Permission.BackupDatabase, "list the backup files");

        if (!Directory.Exists(Folder))
        {
            return Task.FromResult<IReadOnlyList<BackupFile>>([]);
        }

        IReadOnlyList<BackupFile> files = [.. Directory
            .EnumerateFiles(Folder, "*.bak", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .Select(file => new BackupFile(
                file.FullName,
                file.Name,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                file.Length))
            .OrderByDescending(file => file.WrittenAtUtc)];

        return Task.FromResult(files);
    }

    public async Task<OperationResult<BackupFile>> CreateAsync(CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.BackupDatabase, "take a backup of the database");

        try
        {
            Directory.CreateDirectory(Folder);

            var database = await DatabaseNameAsync(cancellationToken).ConfigureAwait(false);
            var path = Path.Combine(
                Folder,
                $"MediStock-{_clock.UtcNow.ToLocalTime():yyyyMMdd-HHmmss}.bak");

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = CommandTimeoutSeconds;
                command.CommandText =
                    $"""
                     BACKUP DATABASE {Quote(database)}
                     TO DISK = @path
                     WITH INIT, CHECKSUM, COPY_ONLY, NAME = @name, DESCRIPTION = @description
                     """;
                command.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 4000) { Value = path });
                command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128)
                {
                    Value = $"MediStock {database}",
                });
                command.Parameters.Add(new SqlParameter("@description", SqlDbType.NVarChar, 512)
                {
                    Value = $"Taken from the application by {_session.CurrentUser?.FriendlyName ?? "an operator"}.",
                });

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var file = new FileInfo(path);
            var taken = new BackupFile(
                file.FullName,
                file.Name,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                file.Length);

            await _audit.RecordAsync(
                new AuditRecord(AuditAction.BackupTaken, $"A backup of {database} was written to {file.Name}.")
                {
                    EntityType = "Backup",
                    EntityId = file.Name,
                    Detail = file.FullName,
                },
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Backup of {Database} written to {Path}.", database, path);

            return OperationResult<BackupFile>.Success(taken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Taking a backup failed.");

            return OperationResult<BackupFile>.Fail(
                "The backup could not be taken. Check that the folder is writable and that SQL Server is "
                + "running, then try again.");
        }
    }

    public async Task<OperationResult> VerifyAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _session.Require(Permission.BackupDatabase, "check a backup file");

        if (!File.Exists(path))
        {
            return OperationResult.Fail($"There is no file at {path}.");
        }

        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM";
            command.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 4000) { Value = path });

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return OperationResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Backup {Path} failed its verification.", path);

            return OperationResult.Fail(
                $"SQL Server could not read {Path.GetFileName(path)} as a backup. The file is damaged or is "
                + "not a MediStock backup.");
        }
    }

    public async Task<OperationResult> RestoreAsync(
        string path,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _session.Require(Permission.BackupDatabase, "restore the database from a backup");

        if (string.IsNullOrWhiteSpace(reason))
        {
            return OperationResult.Fail(
                "A reason is required. The trail records why a restore was done, so that the next person can "
                + "tell a deliberate rollback from a mistake.");
        }

        if (!File.Exists(path))
        {
            return OperationResult.Fail($"There is no file at {path}.");
        }

        var verification = await VerifyAsync(path, cancellationToken).ConfigureAwait(false);
        if (!verification.Succeeded)
        {
            return verification;
        }

        var database = await DatabaseNameAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // This application's own idle contexts would otherwise hold the database open and the restore
            // would wait for them.
            SqlConnection.ClearAllPools();

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                $"ALTER DATABASE {Quote(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE",
                cancellationToken).ConfigureAwait(false);

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandTimeout = CommandTimeoutSeconds;
                command.CommandText = $"RESTORE DATABASE {Quote(database)} FROM DISK = @path WITH REPLACE, RECOVERY";
                command.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 4000) { Value = path });

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await ExecuteAsync(
                    connection,
                    $"ALTER DATABASE {Quote(database)} SET MULTI_USER",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Restoring {Path} failed.", path);

            return OperationResult.Fail(
                $"The restore from {Path.GetFileName(path)} failed. Close any other program using the "
                + "database and try again. Check the application log for details.");
        }

        // Recorded after the restore, so the row survives it only if the restored database already contained
        // it — which it cannot. The log line is therefore the copy that survives, and says so.
        _logger.LogWarning(
            "The database {Database} was restored from {Path}. Every change made after that backup has been "
            + "discarded and the application must be restarted.",
            database,
            path);

        try
        {
            await _audit.RecordAsync(
                new AuditRecord(
                    AuditAction.RestoreCompleted,
                    $"The database was restored from {Path.GetFileName(path)} by "
                    + $"{_session.CurrentUser?.FriendlyName ?? "an operator"}.")
                {
                    EntityType = "Backup",
                    EntityId = Path.GetFileName(path),
                    Reason = reason,
                    Detail = path,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The restore succeeded but the audit row could not be written.");
        }

        return OperationResult.Success();
    }

    /// <summary>
    /// Opens a connection to the same server, but to <c>master</c>: a database cannot be taken offline or
    /// restored from a connection to itself.
    /// </summary>
    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(await ConnectionStringAsync(cancellationToken).ConfigureAwait(false))
        {
            InitialCatalog = "master",
            ConnectTimeout = 30,
        };

        var connection = new SqlConnection(builder.ConnectionString);

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    /// <summary>
    /// The connection string and database name, read from the application's own configured context so that
    /// there is one definition of which database the application uses.
    /// </summary>
    private async Task<string> ConnectionStringAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("The database context has no connection string.");
    }

    private async Task<string> DatabaseNameAsync(CancellationToken cancellationToken)
        => new SqlConnectionStringBuilder(await ConnectionStringAsync(cancellationToken).ConfigureAwait(false)).InitialCatalog;

    /// <summary>
    /// Runs a statement that takes no parameters. Used for <c>ALTER DATABASE</c>, whose target cannot be
    /// parameterised — the identifier is quoted instead, and it comes from the connection string rather than
    /// from anything an operator typed.
    /// </summary>
    private static async Task ExecuteAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Quotes an identifier for SQL Server, doubling any closing bracket inside it.</summary>
    private static string Quote(string identifier)
        => $"[{identifier.Replace("]", "]]")}]";
}
