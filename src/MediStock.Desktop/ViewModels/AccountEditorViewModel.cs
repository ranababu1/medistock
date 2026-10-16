using CommunityToolkit.Mvvm.ComponentModel;
using MediStock.Application.Identity;
using MediStock.Domain.Identity;

namespace MediStock.Desktop.ViewModels;

/// <summary>What the account editor is being used for. Only one thing at a time.</summary>
public enum AccountEditorMode
{
    /// <summary>Nothing is being edited and the panel is hidden.</summary>
    Closed,

    /// <summary>Adding an account.</summary>
    Create,

    /// <summary>Moving an existing account to another role.</summary>
    ChangeRole,

    /// <summary>An administrator sets somebody else's password.</summary>
    ResetPassword,

    /// <summary>The signed-in person changes their own password, proving they know the old one.</summary>
    ChangeOwnPassword,
}

/// <summary>One role as the picker offers it, with what it carries, so the choice is informed.</summary>
public sealed record RoleOption(Role Role, string Label, string Summary)
{
    /// <summary>Every role, in the order the catalogue declares them.</summary>
    public static IReadOnlyList<RoleOption> All { get; } =
    [
        .. RoleCatalog.All.Select(role => new RoleOption(role, role.Label(), RoleCatalog.PermissionsFor(role).Describe())),
    ];

    public override string ToString() => Label;
}

/// <summary>
/// The editor panel on the users screen: everything the four account operations need typed in, in one
/// place, so the view only has to ask which of its rows apply.
/// </summary>
/// <remarks>
/// It holds state and checks it; it does not call the directory. The page owns the calls, because the page
/// also has to refresh the list and report the outcome.
/// </remarks>
public sealed partial class AccountEditorViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(IntroText))]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    [NotifyPropertyChangedFor(nameof(ShowsAccountSummary))]
    [NotifyPropertyChangedFor(nameof(ShowsUserName))]
    [NotifyPropertyChangedFor(nameof(ShowsDisplayName))]
    [NotifyPropertyChangedFor(nameof(ShowsRole))]
    [NotifyPropertyChangedFor(nameof(ShowsNewPassword))]
    [NotifyPropertyChangedFor(nameof(ShowsConfirmPassword))]
    [NotifyPropertyChangedFor(nameof(ShowsCurrentPassword))]
    [NotifyPropertyChangedFor(nameof(ShowsReason))]
    private AccountEditorMode mode = AccountEditorMode.Closed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAccountSummary))]
    [NotifyPropertyChangedFor(nameof(AccountSummary))]
    private UserDto? account;

    [ObservableProperty]
    private string userName = string.Empty;

    [ObservableProperty]
    private string displayName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRoleSummary))]
    [NotifyPropertyChangedFor(nameof(Role))]
    [NotifyPropertyChangedFor(nameof(RoleWouldChange))]
    private RoleOption? selectedRole;

    [ObservableProperty]
    private string password = string.Empty;

    [ObservableProperty]
    private string confirmPassword = string.Empty;

    [ObservableProperty]
    private string currentPassword = string.Empty;

    [ObservableProperty]
    private string reason = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    private string? validationMessage;

    public IReadOnlyList<RoleOption> Roles => RoleOption.All;

    public bool IsOpen => Mode != AccountEditorMode.Closed;

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    /// <summary>The role that would be granted if the editor were saved.</summary>
    public Role Role => SelectedRole?.Role ?? Domain.Identity.Role.Cashier;

    public string SelectedRoleSummary => SelectedRole?.Summary ?? string.Empty;

    /// <summary>What the panel says it is doing, as its heading.</summary>
    public string Title => Mode switch
    {
        AccountEditorMode.Create => "New account",
        AccountEditorMode.ChangeRole => "Change role",
        AccountEditorMode.ResetPassword => "Reset password",
        AccountEditorMode.ChangeOwnPassword => "Change your password",
        _ => string.Empty,
    };

    /// <summary>One line under the heading saying what the operation will and will not do.</summary>
    public string IntroText => Mode switch
    {
        AccountEditorMode.Create =>
            "The account can sign in as soon as it is created. Give the password to its owner in person; it is "
            + "never stored in the clear and never written to the audit trail.",
        AccountEditorMode.ChangeRole =>
            "The role decides what this account may do. The change is audited, and the installation must keep "
            + "at least one active administrator.",
        AccountEditorMode.ResetPassword =>
            "You do not have to know the old password, which is why this is recorded separately from a person "
            + "changing their own.",
        AccountEditorMode.ChangeOwnPassword =>
            "Your current password is required, and the session survives the change.",
        _ => string.Empty,
    };

    public string SaveButtonText => Mode switch
    {
        AccountEditorMode.Create => "Create account",
        AccountEditorMode.ChangeRole => "Change role",
        AccountEditorMode.ResetPassword => "Set password",
        AccountEditorMode.ChangeOwnPassword => "Change password",
        _ => "Save",
    };

    /// <summary>A new account shows the name it will sign in with; an existing one shows it as a heading.</summary>
    public bool ShowsUserName => Mode == AccountEditorMode.Create;

    public bool ShowsDisplayName => Mode == AccountEditorMode.Create;

    public bool ShowsRole => Mode is AccountEditorMode.Create or AccountEditorMode.ChangeRole;

    public bool ShowsNewPassword =>
        Mode is AccountEditorMode.Create or AccountEditorMode.ResetPassword or AccountEditorMode.ChangeOwnPassword;

    public bool ShowsConfirmPassword => ShowsNewPassword;

    public bool ShowsCurrentPassword => Mode == AccountEditorMode.ChangeOwnPassword;

    public bool ShowsReason => Mode is AccountEditorMode.Create or AccountEditorMode.ChangeRole or AccountEditorMode.ResetPassword;

    /// <summary>True when the panel is about an account that already exists, so it can name it.</summary>
    public bool ShowsAccountSummary => Account is not null;

    /// <summary>A short line naming the account being changed, and its present role.</summary>
    public string AccountSummary => Account is null
        ? string.Empty
        : $"{Account.FriendlyName} · {Account.UserName} · {Account.RoleLabel}";

    /// <summary>Opens the panel for a new account, with the safest role preselected.</summary>
    public void StartCreate()
    {
        Reset();
        Mode = AccountEditorMode.Create;
        SelectedRole = Roles.FirstOrDefault(option => option.Role == Domain.Identity.Role.Cashier) ?? Roles[0];
    }

    /// <summary>Opens the panel to move an account to another role.</summary>
    public void StartChangeRole(UserDto account)
    {
        ArgumentNullException.ThrowIfNull(account);

        Reset();
        Mode = AccountEditorMode.ChangeRole;
        Account = account;
        SelectedRole = Roles.FirstOrDefault(option => option.Role == account.Role) ?? Roles[0];
    }

    /// <summary>Opens the panel for an administrator to set somebody else's password.</summary>
    public void StartResetPassword(UserDto account)
    {
        ArgumentNullException.ThrowIfNull(account);

        Reset();
        Mode = AccountEditorMode.ResetPassword;
        Account = account;
    }

    /// <summary>Opens the panel for the signed-in person to choose a new password.</summary>
    public void StartChangeOwnPassword()
    {
        Reset();
        Mode = AccountEditorMode.ChangeOwnPassword;
    }

    /// <summary>Closes the panel and forgets everything typed into it.</summary>
    public void Close()
    {
        Reset();
        Mode = AccountEditorMode.Closed;
    }

    /// <summary>Moves a list of problems onto the panel, or clears it.</summary>
    public void ShowProblems(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        ValidationMessage = problems.Count == 0 ? null : string.Join(Environment.NewLine, problems);
    }

    /// <summary>Moves a refusal onto the panel, so a failed save explains itself where it happened.</summary>
    public void ShowProblem(string? message) =>
        ValidationMessage = string.IsNullOrWhiteSpace(message) ? null : message.Trim();

    public void ClearProblems() => ValidationMessage = null;

    /// <summary>
    /// The draft to create, with the password the operator typed. The name is trimmed, because a trailing
    /// space in an account name is invisible and would make the next sign-in fail for no visible reason.
    /// </summary>
    public UserDraft ToCreateDraft() => new()
    {
        UserName = UserName.Trim(),
        DisplayName = DisplayName.Trim(),
        Role = Role,
        Password = Password,
        ConfirmPassword = ConfirmPassword,
        Reason = Reason.Trim(),
        RequiresPassword = true,
    };

    /// <summary>Every problem with a new account, in the order they should be fixed.</summary>
    public IReadOnlyList<string> ValidateCreate() => ToCreateDraft().Validate();

    /// <summary>Every problem with a password being set, checked against the account it belongs to.</summary>
    public IReadOnlyList<string> ValidatePasswordChange(string accountName, bool needsCurrentPassword)
    {
        var problems = new List<string>();

        if (needsCurrentPassword && string.IsNullOrEmpty(CurrentPassword))
        {
            problems.Add("Your current password is required.");
        }

        if (string.IsNullOrEmpty(Password))
        {
            problems.Add("A new password is required.");
        }

        if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
        {
            problems.Add("The two passwords do not match.");
        }

        problems.AddRange(PasswordPolicy.Validate(Password, accountName));

        return problems;
    }

    /// <summary>True when the chosen role differs from the one the account already holds.</summary>
    public bool RoleWouldChange => Account is not null && Account.Role != Role;

    /// <summary>Forgets everything on the panel.</summary>
    private void Reset()
    {
        Account = null;
        UserName = string.Empty;
        DisplayName = string.Empty;
        SelectedRole = null;
        Password = string.Empty;
        ConfirmPassword = string.Empty;
        CurrentPassword = string.Empty;
        Reason = string.Empty;
        ValidationMessage = null;
    }
}
