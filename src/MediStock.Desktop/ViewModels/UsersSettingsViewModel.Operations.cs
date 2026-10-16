using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Identity;
using MediStock.Application.Operations;
using MediStock.Desktop.Logging;
using MediStock.Domain.Identity;
using Microsoft.Extensions.Logging;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// The backup and support sections of the Users &amp; Settings page: copies of the database that can be put
/// back, and the facts a support call starts from.
/// </summary>
/// <remarks>
/// Restoring replaces the database under the running application, so the section treats it as two steps and
/// says so: restore, then restart. The service reports the same thing rather than pretending it can swap a
/// database that open screens are holding.
/// </remarks>
public sealed partial class UsersSettingsViewModel
{
    // ---------------------------------------------------------------- backups

    /// <summary>Where backups are written and read from. Shown, because a backup nobody can find is not one.</summary>
    public string BackupFolder { get; }

    /// <summary>The backup files on disk, newest first.</summary>
    public ObservableCollection<BackupFile> Backups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanVerifySelectedBackup))]
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedBackup))]
    [NotifyPropertyChangedFor(nameof(SelectedBackupSummary))]
    private BackupFile? selectedBackup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoBackups))]
    private string backupSummary = string.Empty;

    [ObservableProperty]
    private string? backupMessage;

    /// <summary>Set once a restore has succeeded: what the application needs next is a restart, not a click.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestorePending))]
    private string? restoreNotice;

    public bool HasNoBackups => Backups.Count == 0;

    public bool HasRestorePending => !string.IsNullOrWhiteSpace(RestoreNotice);

    /// <summary>A backup can be checked or put back only by an operator who may take one, and only once one is chosen.</summary>
    public bool CanVerifySelectedBackup => CanBackUp && SelectedBackup is not null;

    public bool CanRestoreSelectedBackup => CanBackUp && SelectedBackup is not null;

    /// <summary>
    /// The refusal note shows when the permission is missing. XAML has no negating converter, and the note
    /// belongs to the view model's vocabulary anyway: it is the same sentence the service would refuse with.
    /// </summary>
    public bool CannotBackUp => !CanBackUp;

    /// <summary>What the chosen file is, in one line, before anything is done with it.</summary>
    public string SelectedBackupSummary => SelectedBackup is { } file
        ? $"{file.FileName} · {file.SizeText} · written {file.WrittenAtUtc.ToLocalTime():dd MMM yyyy HH:mm}"
        : "Choose a backup to check or restore.";

    /// <summary>Lists the backup files, newest first.</summary>
    [RelayCommand]
    private async Task LoadBackupsAsync(CancellationToken cancellationToken)
    {
        BackupMessage = null;

        try
        {
            var files = await _backup.ListAsync(cancellationToken).ConfigureAwait(true);

            var previous = SelectedBackup?.Path;

            Backups.Clear();
            foreach (var file in files)
            {
                Backups.Add(file);
            }

            SelectedBackup = Backups.FirstOrDefault(f => f.Path == previous) ?? Backups.FirstOrDefault();

            BackupSummary = Backups.Count == 0
                ? $"No backups in {BackupFolder} yet."
                : $"{Backups.Count} backup file(s) in {BackupFolder}";

            OnPropertyChanged(nameof(HasNoBackups));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Listing the backup files failed.");
            Backups.Clear();
            BackupSummary = string.Empty;
            BackupMessage = Application.Common.OperationResult.From(ex).Message;
            OnPropertyChanged(nameof(HasNoBackups));
        }
    }

    /// <summary>Writes a full copy of the database, without asking: taking a backup is never the risky choice.</summary>
    [RelayCommand]
    private async Task TakeBackupAsync(CancellationToken cancellationToken)
    {
        ClearError();
        BackupMessage = null;
        StatusMessage = null;

        if (!CanBackUp)
        {
            BackupMessage = BackupRefusalNote;
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _backup.CreateAsync(cancellationToken).ConfigureAwait(true);

            if (!result.Succeeded || result.Value is null)
            {
                BackupMessage = result.Message;
                return;
            }

            StatusMessage = $"Backed up the database to {result.Value.FileName} ({result.Value.SizeText}).";

            await LoadBackupsAsync(cancellationToken).ConfigureAwait(true);

            SelectedBackup = Backups.FirstOrDefault(f => f.Path == result.Value.Path) ?? SelectedBackup;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Taking a backup failed.");
            BackupMessage = Application.Common.OperationResult.From(ex).Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Asks SQL Server to read the whole file, so a restore is only attempted on a file that works.</summary>
    [RelayCommand]
    private async Task VerifyBackupAsync(CancellationToken cancellationToken)
    {
        ClearError();
        BackupMessage = null;

        if (SelectedBackup is not { } file)
        {
            BackupMessage = "Choose a backup to check first.";
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _backup.VerifyAsync(file.Path, cancellationToken).ConfigureAwait(true);

            BackupMessage = result.Succeeded
                ? $"{file.FileName} is a readable backup of this database. Writing it back would undo everything recorded since it was taken."
                : result.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking the backup {File} failed.", file.FileName);
            BackupMessage = Application.Common.OperationResult.From(ex).Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Puts a backup back. It asks twice — once for the reason, once for a confirmation that names the date
    /// being rolled back to — because everything recorded since that date is about to be discarded.
    /// </summary>
    [RelayCommand]
    private async Task RestoreBackupAsync(CancellationToken cancellationToken)
    {
        ClearError();
        BackupMessage = null;
        RestoreNotice = null;

        if (SelectedBackup is not { } file)
        {
            BackupMessage = "Choose a backup to restore first.";
            return;
        }

        var reason = _dialogs.Prompt(
            "Restore backup",
            $"Why is the database being rolled back to {file.WrittenAtUtc.ToLocalTime():dd MMM yyyy HH:mm}?\n\n"
            + "Recorded in the audit trail. Nothing recorded after that moment survives the restore.",
            string.Empty);

        if (string.IsNullOrWhiteSpace(reason))
        {
            BackupMessage = "A restore needs a reason. Nothing was restored.";
            return;
        }

        var confirmed = _dialogs.Confirm(
            "Restore backup",
            $"Replace the whole database with {file.FileName}?\n\n"
            + "Every sale, receipt, return and account change recorded since "
            + $"{file.WrittenAtUtc.ToLocalTime():dd MMM yyyy HH:mm} will be gone. The application must be restarted afterwards.");

        if (!confirmed)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _backup.RestoreAsync(file.Path, reason.Trim(), cancellationToken).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                BackupMessage = result.Message;
                return;
            }

            RestoreNotice = $"The database was replaced with {file.FileName}. Close MediStock and start it again: "
                + "nothing on this screen is reading the restored data until you do.";

            await LoadBackupsAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restoring the backup {File} failed.", file.FileName);
            BackupMessage = Application.Common.OperationResult.From(ex).Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens the backup folder, so an operator can copy a file to another disk.</summary>
    [RelayCommand]
    private void OpenBackupFolder() => Reveal(BackupFolder, "the backup folder");

    // ---------------------------------------------------------------- support

    /// <summary>Where the logs are, and which file to send when asking for help.</summary>
    public string LogFolder { get; }

    /// <summary>Today's log file, which is the one a support call wants.</summary>
    public string LogFilePath => FileLoggerProvider.CurrentLogFile;

    public string ApplicationSummary => ProductInfo.Summary;

    /// <summary>The pharmacy's own name and the business date it is working on, which the day's figures group by.</summary>
    public string PharmacySummary => $"{_options.Value.PharmacyName} · business date {_clock.BusinessDate:yyyy-MM-dd} "
        + $"· {_options.Value.TimeZoneId}";

    [ObservableProperty]
    private string databaseSummary = "Not checked yet.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyMigrations))]
    private bool hasPendingMigrations;

    [ObservableProperty]
    private string? supportMessage;

    /// <summary>The newest few trail rows, shown without needing the trail permission: a support call may need them.</summary>
    public ObservableCollection<AuditEntryDto> RecentAuditRows { get; } = [];

    /// <summary>
    /// Applying migrations changes the schema, so the button is offered to administrators. The application
    /// also applies them at startup when it is configured to, which is why the service itself carries no
    /// permission check — it runs before anybody has signed in.
    /// </summary>
    public bool CanApplyMigrations => CanManageUsers && HasPendingMigrations;

    private static string Version => ProductInfo.Version;

    /// <summary>Probes the database and reports what it found, in the words an operator can act on.</summary>
    [RelayCommand]
    private async Task CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        SupportMessage = null;

        try
        {
            var report = await _health.CheckAsync(cancellationToken).ConfigureAwait(true);

            DatabaseSummary = report.IsReachable
                ? $"{report.Provider} · {report.DatabaseName} · {report.ServerVersion} · answered in {report.Elapsed.TotalMilliseconds:N0} ms"
                : $"Not reachable: {report.Error}";

            HasPendingMigrations = report.HasPendingMigrations;

            SupportMessage = report.HasPendingMigrations
                ? $"{report.PendingMigrations.Count} migration(s) have not been applied: {string.Join(", ", report.PendingMigrations)}"
                : report.IsReachable
                    ? "The schema is up to date."
                    : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking the database failed.");
            DatabaseSummary = "The check itself failed.";
            SupportMessage = Application.Common.OperationResult.From(ex).Message;
        }
    }

    /// <summary>Applies the migrations that have not been applied, and reports how many ran.</summary>
    [RelayCommand]
    private async Task ApplyMigrationsAsync(CancellationToken cancellationToken)
    {
        SupportMessage = null;

        if (!CanManageUsers)
        {
            SupportMessage = Domain.Identity.PermissionSet.Explain(Permission.ManageUsers, "apply changes to the database schema");
            return;
        }

        IsBusy = true;

        try
        {
            var applied = await _health.ApplyPendingMigrationsAsync(cancellationToken).ConfigureAwait(true);

            SupportMessage = applied == 0
                ? "The schema was already up to date."
                : $"Applied {applied} migration(s).";

            await CheckDatabaseAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying the pending migrations failed.");
            SupportMessage = Application.Common.OperationResult.From(ex).Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens the folder holding the logs.</summary>
    [RelayCommand]
    private void OpenLogFolder() => Reveal(LogFolder, "the log folder");

    /// <summary>Reads the newest trail rows for the support panel.</summary>
    [RelayCommand]
    private async Task LoadRecentAuditAsync(CancellationToken cancellationToken)
    {
        SupportMessage = null;

        try
        {
            var rows = await _audit.RecentAsync(15, cancellationToken).ConfigureAwait(true);

            RecentAuditRows.Clear();
            foreach (var row in rows)
            {
                RecentAuditRows.Add(row);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the newest audit rows failed.");
            RecentAuditRows.Clear();
            SupportMessage = Application.Common.OperationResult.From(ex).Message;
        }
    }

    /// <summary>Loads the two sections that need nothing but a working database.</summary>
    private async Task LoadSupportAsync(CancellationToken cancellationToken)
    {
        await CheckDatabaseAsync(cancellationToken).ConfigureAwait(true);
        await LoadRecentAuditAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Loads the audit section, which is its own query with its own filters.</summary>
    private Task LoadAuditAsync(CancellationToken cancellationToken) =>
        RunAuditQueryAsync(cancellationToken);

    /// <summary>Opens a folder in File Explorer, reporting rather than throwing when it cannot be opened.</summary>
    private void Reveal(string folder, string what)
    {
        try
        {
            Directory.CreateDirectory(folder);

            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Opening {What} at {Folder} failed.", what, folder);
            SupportMessage = $"Could not open {what} at {folder}.";
        }
    }
}
