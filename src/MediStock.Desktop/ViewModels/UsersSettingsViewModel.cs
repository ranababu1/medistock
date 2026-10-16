using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Common;
using MediStock.Application.Configuration;
using MediStock.Application.Diagnostics;
using MediStock.Application.Identity;
using MediStock.Application.Operations;
using MediStock.Desktop.Services;
using MediStock.Desktop.Logging;
using MediStock.Domain.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// The Users &amp; Settings page: accounts, the audit trail, backups, and the facts a support call starts from.
/// </summary>
/// <remarks>
/// <para>
/// It is one page rather than four because all four answer the same question — "how is this installation set
/// up, and what has it been doing?" — and because each of them needs the same operator to be an
/// administrator, except the support panel.
/// </para>
/// <para>
/// Sections load independently and keep their own message, so a database that refuses the audit query still
/// lets the operator read the backup list and see which log file to send. Nothing on the page decides for
/// itself whether the operator may act: the services require the permission and the screen only hides what
/// would certainly be refused. Hiding a button is convenience; the refusal is the rule.
/// </para>
/// </remarks>
public sealed partial class UsersSettingsViewModel : ViewModelBase
{
    private readonly IUserDirectory _users;
    private readonly IAuthenticationService _authentication;
    private readonly IAuditTrail _audit;
    private readonly IBackupService _backup;
    private readonly IDatabaseHealthCheck _health;
    private readonly IUserSession _session;
    private readonly IDialogService _dialogs;
    private readonly IClock _clock;
    private readonly IOptions<MediStockOptions> _options;
    private readonly ILogger<UsersSettingsViewModel> _logger;

    public UsersSettingsViewModel(
        IUserDirectory users,
        IAuthenticationService authentication,
        IAuditTrail audit,
        IBackupService backup,
        IDatabaseHealthCheck health,
        IUserSession session,
        IDialogService dialogs,
        IClock clock,
        IOptions<MediStockOptions> options,
        ILogger<UsersSettingsViewModel> logger)
        : base("Users & Settings")
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        _users = users;
        _authentication = authentication;
        _audit = audit;
        _backup = backup;
        _health = health;
        _session = session;
        _dialogs = dialogs;
        _clock = clock;
        _options = options;
        _logger = logger;

        BackupFolder = backup.Folder;
        LogFolder = FileLoggerProvider.DefaultDirectory;

        // A week of trail is what a support call asks for: long enough to contain the mistake, short enough
        // that the screen fills with pages rather than months.
        AuditFrom = clock.BusinessDate.AddDays(-6);
        AuditTo = clock.BusinessDate;
    }

    /// <summary>The account editor panel, closed until something opens it.</summary>
    public AccountEditorViewModel Editor { get; } = new();

    // ---------------------------------------------------------------- who is signed in

    /// <summary>True when this operator may see and change accounts.</summary>
    public bool CanManageUsers => _session.Can(Permission.ManageUsers);

    /// <summary>True when this operator may read the audit trail.</summary>
    public bool CanViewAuditTrail => _session.Can(Permission.ViewAuditTrail);

    /// <summary>True when this operator may take and restore backups.</summary>
    public bool CanBackUp => _session.Can(Permission.BackupDatabase);

    /// <summary>A note explaining a section this operator may not use, in the application's own words.</summary>
    public string AccountsRefusalNote =>
        PermissionSet.Explain(Permission.ManageUsers, "see and change the accounts on this installation");

    /// <summary>A note explaining why the audit trail is not readable.</summary>
    public string AuditRefusalNote =>
        PermissionSet.Explain(Permission.ViewAuditTrail, "read the audit trail");

    /// <summary>A note explaining why backups are out of reach.</summary>
    public string BackupRefusalNote =>
        PermissionSet.Explain(Permission.BackupDatabase, "take and restore backups of the database");

    /// <summary>Who is signed in, as a heading for the settings page.</summary>
    public string OperatorSummary => _session.CurrentUser is { } user
        ? $"{user.FriendlyName} · {user.UserName} · {user.RoleLabel}"
        : "Nobody is signed in.";

    /// <summary>What the signed-in account may do, written out, so the page explains itself.</summary>
    public string OperatorPermissionSummary => _session.CurrentUser is { } user
        ? user.Permissions.Describe()
        : string.Empty;

    // ---------------------------------------------------------------- accounts

    /// <summary>The accounts on this installation, as configured.</summary>
    public ObservableCollection<UserDto> Accounts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedAccountIsSelf))]
    [NotifyPropertyChangedFor(nameof(CanEditSelectedAccount))]
    [NotifyPropertyChangedFor(nameof(CanResetSelectedAccountPassword))]
    [NotifyPropertyChangedFor(nameof(CanToggleSelectedAccount))]
    [NotifyPropertyChangedFor(nameof(ToggleActiveButtonText))]
    [NotifyPropertyChangedFor(nameof(SelectedAccountPermissionSummary))]
    private UserDto? selectedAccount;

    [ObservableProperty]
    private string accountSearchTerm = string.Empty;

    [ObservableProperty]
    private bool includeInactiveAccounts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoAccounts))]
    private string? accountsMessage;

    [ObservableProperty]
    private string accountsSummary = string.Empty;

    public bool HasNoAccounts => Accounts.Count == 0;

    /// <summary>True when the account in the list is the one making the change.</summary>
    public bool SelectedAccountIsSelf => SelectedAccount is { } account && account.Id == _session.CurrentUser?.Id;

    /// <summary>
    /// A role can be changed for anybody but yourself, and only if this operator may change accounts at all.
    /// </summary>
    public bool CanEditSelectedAccount => CanManageUsers && SelectedAccount is not null && !SelectedAccountIsSelf;

    /// <summary>
    /// A password can be set for somebody else, but not for yourself: your own goes through the panel that
    /// asks for the current password first.
    /// </summary>
    public bool CanResetSelectedAccountPassword => CanEditSelectedAccount;

    /// <summary>An account can be stopped or started, but not the one currently signed in.</summary>
    public bool CanToggleSelectedAccount => CanEditSelectedAccount;

    /// <summary>The button offers the opposite of what the account is doing now.</summary>
    public string ToggleActiveButtonText => SelectedAccount is { IsActive: false } ? "Reactivate" : "Deactivate";

    /// <summary>What the chosen account may do, so a role change can be read before it is made.</summary>
    public string SelectedAccountPermissionSummary => SelectedAccount is { } account
        ? account.Permissions.Describe()
        : string.Empty;

    [ObservableProperty]
    private string? statusMessage;

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    public override async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await ReloadAllAsync(cancellationToken).ConfigureAwait(true);

    /// <summary>
    /// Refreshes every section. Each one that fails reports itself and leaves the others alone, so a broken
    /// audit query does not hide the log path.
    /// </summary>
    [RelayCommand]
    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        ClearError();
        StatusMessage = null;
        await ReloadAllAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Re-reads the account list, honouring the search box and the include-inactive switch.</summary>
    [RelayCommand]
    private async Task LoadAccountsAsync(CancellationToken cancellationToken)
    {
        ClearError();
        AccountsMessage = null;

        if (!CanManageUsers)
        {
            Accounts.Clear();
            AccountsSummary = string.Empty;
            AccountsMessage = AccountsRefusalNote;
            OnPropertyChanged(nameof(HasNoAccounts));
            return;
        }

        IsBusy = true;

        try
        {
            var query = new UserQuery
            {
                Term = string.IsNullOrWhiteSpace(AccountSearchTerm) ? null : AccountSearchTerm.Trim(),
                IncludeInactive = IncludeInactiveAccounts,
            };

            var found = await _users.SearchAsync(query, cancellationToken).ConfigureAwait(true);

            var previous = SelectedAccount?.Id;

            Accounts.Clear();
            foreach (var account in found)
            {
                Accounts.Add(account);
            }

            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == previous) ?? Accounts.FirstOrDefault();

            AccountsSummary = Accounts.Count == 0
                ? "No accounts match."
                : query.IsEmpty
                    ? $"{Accounts.Count} account(s)"
                    : $"{Accounts.Count} of at most {UserQuery.MaxResults} matching account(s)";

            OnPropertyChanged(nameof(HasNoAccounts));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Listing the accounts failed.");
            Accounts.Clear();
            AccountsSummary = string.Empty;
            OnPropertyChanged(nameof(HasNoAccounts));
            AccountsMessage = Application.Common.OperationResult.From(ex).Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Clears the search box and lists everything again.</summary>
    [RelayCommand]
    private async Task ClearAccountSearchAsync(CancellationToken cancellationToken)
    {
        AccountSearchTerm = string.Empty;
        IncludeInactiveAccounts = false;
        await LoadAccountsAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Opens the panel for a new account.</summary>
    [RelayCommand]
    private void NewAccount()
    {
        StatusMessage = null;
        Editor.StartCreate();
    }

    /// <summary>Opens the panel to move the selected account to another role.</summary>
    [RelayCommand]
    private void ChangeRole()
    {
        if (SelectedAccount is not { } account)
        {
            return;
        }

        StatusMessage = null;
        Editor.StartChangeRole(account);
    }

    /// <summary>Opens the panel to set the selected account's password.</summary>
    [RelayCommand]
    private void ResetPassword()
    {
        if (SelectedAccount is not { } account)
        {
            return;
        }

        StatusMessage = null;
        Editor.StartResetPassword(account);
    }

    /// <summary>Opens the panel for the signed-in operator to choose a new password.</summary>
    [RelayCommand]
    private void ChangeOwnPassword()
    {
        StatusMessage = null;
        Editor.StartChangeOwnPassword();
    }

    /// <summary>Closes the editor panel without saving.</summary>
    [RelayCommand]
    private void CancelEditor() => Editor.Close();

    /// <summary>
    /// Stops or allows the selected account. Deactivating asks first: the account keeps its history, but
    /// nobody can use it until somebody puts it back.
    /// </summary>
    [RelayCommand]
    private async Task ToggleActiveAsync(CancellationToken cancellationToken)
    {
        if (SelectedAccount is not { } account)
        {
            return;
        }

        StatusMessage = null;

        var reactivating = !account.IsActive;

        if (!reactivating && !_dialogs.Confirm(
                "Deactivate account",
                $"Stop \"{account.FriendlyName}\" ({account.UserName}) signing in?\n\n"
                + "The account and everything it has done stay in the records. An administrator can reactivate it later."))
        {
            return;
        }

        IsBusy = true;

        try
        {
            var reason = _dialogs.Prompt(
                reactivating ? "Reactivate account" : "Deactivate account",
                "Why is this account being changed? Recorded in the audit trail.",
                string.Empty);

            if (reason is null)
            {
                return;
            }

            var result = await _users
                .SetActiveAsync(account.Id, reactivating, reason, cancellationToken)
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                AccountsMessage = result.Message;
                return;
            }

            StatusMessage = reactivating
                ? $"\"{account.FriendlyName}\" may sign in again."
                : $"\"{account.FriendlyName}\" may no longer sign in.";

            await LoadAccountsAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Changing whether {UserName} is active failed.", account.UserName);
            AccountsMessage = Application.Common.OperationResult.From(ex).Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Saves whatever the editor panel is open for. One button, four operations, because the panel already
    /// knows which one it is showing.
    /// </summary>
    [RelayCommand]
    private async Task SaveEditorAsync(CancellationToken cancellationToken)
    {
        Editor.ClearProblems();
        StatusMessage = null;
        IsBusy = true;

        try
        {
            switch (Editor.Mode)
            {
                case AccountEditorMode.Create:
                    await CreateAccountAsync(cancellationToken).ConfigureAwait(true);
                    break;

                case AccountEditorMode.ChangeRole:
                    await ChangeRoleAsync(cancellationToken).ConfigureAwait(true);
                    break;

                case AccountEditorMode.ResetPassword:
                    await ResetSelectedPasswordAsync(cancellationToken).ConfigureAwait(true);
                    break;

                case AccountEditorMode.ChangeOwnPassword:
                    await ChangeOwnPasswordAsync(cancellationToken).ConfigureAwait(true);
                    break;

                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the account editor failed.");
            Editor.ShowProblem(Application.Common.OperationResult.From(ex).Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CreateAccountAsync(CancellationToken cancellationToken)
    {
        if (Editor.ValidateCreate() is { Count: > 0 } problems)
        {
            Editor.ShowProblems(problems);
            return;
        }

        var draft = Editor.ToCreateDraft();
        var result = await _users.CreateAsync(draft, cancellationToken).ConfigureAwait(true);

        if (!result.Succeeded)
        {
            Editor.ShowProblem(result.Message);
            return;
        }

        var created = result.Value;
        Editor.Close();

        StatusMessage = $"Created \"{draft.DisplayName}\" ({draft.UserName}) as {draft.Role.Label()}. "
            + "Tell them the password in person; nothing on this screen will show it again.";

        _logger.LogInformation("Account {UserName} was created with the role {Role}.", draft.UserName, draft.Role);

        await LoadAccountsAsync(cancellationToken).ConfigureAwait(true);

        if (created is not null)
        {
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == created.Id) ?? SelectedAccount;
        }
    }

    private async Task ChangeRoleAsync(CancellationToken cancellationToken)
    {
        if (Editor.Account is not { } account)
        {
            return;
        }

        if (!Editor.RoleWouldChange)
        {
            Editor.ShowProblem($"\"{account.UserName}\" already has the {account.RoleLabel} role.");
            return;
        }

        var role = Editor.Role;
        var reason = string.IsNullOrWhiteSpace(Editor.Reason) ? null : Editor.Reason.Trim();

        var result = await _users.SetRoleAsync(account.Id, role, reason, cancellationToken).ConfigureAwait(true);

        if (!result.Succeeded)
        {
            Editor.ShowProblem(result.Message);
            return;
        }

        Editor.Close();
        StatusMessage = $"\"{account.FriendlyName}\" is now {role.Label()}. "
            + "The change takes effect the next time they sign in.";

        await LoadAccountsAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task ResetSelectedPasswordAsync(CancellationToken cancellationToken)
    {
        if (Editor.Account is not { } account)
        {
            return;
        }

        if (Editor.ValidatePasswordChange(account.UserName, needsCurrentPassword: false) is { Count: > 0 } problems)
        {
            Editor.ShowProblems(problems);
            return;
        }

        var reason = string.IsNullOrWhiteSpace(Editor.Reason) ? null : Editor.Reason.Trim();

        var result = await _users
            .ResetPasswordAsync(account.Id, Editor.Password, reason, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Succeeded)
        {
            Editor.ShowProblem(result.Message);
            return;
        }

        Editor.Close();
        StatusMessage = $"Set a new password for \"{account.FriendlyName}\". Give it to them in person.";

        _logger.LogInformation("Password for {UserName} was reset.", account.UserName);

        await LoadAccountsAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task ChangeOwnPasswordAsync(CancellationToken cancellationToken)
    {
        var userName = _session.CurrentUser?.UserName ?? string.Empty;

        if (Editor.ValidatePasswordChange(userName, needsCurrentPassword: true) is { Count: > 0 } problems)
        {
            Editor.ShowProblems(problems);
            return;
        }

        var result = await _authentication
            .ChangeOwnPasswordAsync(Editor.CurrentPassword, Editor.Password, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Succeeded)
        {
            Editor.ShowProblem(result.Message);
            return;
        }

        Editor.Close();
        StatusMessage = "Your password has been changed. It is the one to use at the next sign-in.";
    }

    /// <summary>Reloads the four sections in turn, each reporting its own failure.</summary>
    private async Task ReloadAllAsync(CancellationToken cancellationToken)
    {
        await LoadAccountsAsync(cancellationToken).ConfigureAwait(true);
        await LoadAuditAsync(cancellationToken).ConfigureAwait(true);
        await LoadBackupsAsync(cancellationToken).ConfigureAwait(true);
        await LoadSupportAsync(cancellationToken).ConfigureAwait(true);
    }
}
