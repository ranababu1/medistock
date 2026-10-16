using MediStock.Application.Common;

namespace MediStock.Application.Operations;

/// <summary>One backup file on disk, as the settings screen lists it.</summary>
public sealed record BackupFile(string Path, string FileName, DateTimeOffset WrittenAtUtc, long SizeBytes)
{
    /// <summary>The size in the largest unit that still reads as a whole number, for a list column.</summary>
    public string SizeText => SizeBytes switch
    {
        >= 1_073_741_824 => $"{SizeBytes / 1_073_741_824d:0.##} GB",
        >= 1_048_576 => $"{SizeBytes / 1_048_576d:0.##} MB",
        >= 1024 => $"{SizeBytes / 1024d:0.##} KB",
        _ => $"{SizeBytes} bytes",
    };
}

/// <summary>
/// Copying the database out and putting one back.
/// </summary>
/// <remarks>
/// <para>
/// A backup is the pharmacy's only protection against a lost disk, a failed upgrade or a mistaken stock
/// correction, so both directions are audited and both require
/// <see cref="MediStock.Domain.Identity.Permission.BackupDatabase"/>. Restoring replaces everything that has
/// happened since the file was written, so it also has to carry a reason: the row in the trail is what tells
/// the next person why October's takings vanished.
/// </para>
/// <para>
/// Restoring cannot be done while the application is using the database, so the workflow is deliberately
/// two steps: restore, then restart. The service reports that rather than pretending it can hot-swap a
/// database an open screen is holding.
/// </para>
/// </remarks>
public interface IBackupService
{
    /// <summary>The folder backups are written to and listed from.</summary>
    string Folder { get; }

    /// <summary>Backups in <see cref="Folder"/>, newest first.</summary>
    Task<IReadOnlyList<BackupFile>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes a new backup and returns what was written.</summary>
    Task<OperationResult<BackupFile>> CreateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks SQL Server to read the whole file and report whether it is a usable backup. Refuses before
    /// restoring rather than failing half way through one.
    /// </summary>
    Task<OperationResult> VerifyAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the current database with the contents of <paramref name="path"/>. Every change since that
    /// backup is discarded, which is why <paramref name="reason"/> is required and recorded.
    /// </summary>
    Task<OperationResult> RestoreAsync(string path, string reason, CancellationToken cancellationToken = default);
}
