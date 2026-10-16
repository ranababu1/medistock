using CommunityToolkit.Mvvm.ComponentModel;
using MediStock.Application.Common;
using MediStock.Application.Identity;
using MediStock.Domain.Identity;
using Microsoft.Extensions.Logging;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// Backs the sign-in window. It covers both jobs that window has: creating the first administrator on a
/// fresh installation, and signing somebody in afterwards. Which one is needed is decided by asking the
/// authentication service whether any account exists yet.
/// </summary>
/// <remarks>
/// The window itself stays free of rules — it collects a name and a password and hands them here. A
/// refusal is shown in the same words no matter which account name was tried, so this screen cannot be
/// used to discover who works here.
/// </remarks>
public sealed partial class SignInViewModel : ViewModelBase
{
    private readonly IAuthenticationService _authentication;
    private readonly ILogger<SignInViewModel> _logger;

    public SignInViewModel(IAuthenticationService authentication, ILogger<SignInViewModel> logger)
        : base("MediStock")
    {
        _authentication = authentication;
        _logger = logger;
    }

    /// <summary>True when this installation has no accounts yet, so the first administrator must be created.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadlineText))]
    [NotifyPropertyChangedFor(nameof(IntroText))]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    [NotifyPropertyChangedFor(nameof(IsSignInMode))]
    private bool isFirstRun;

    /// <summary>True while the accounts are being counted or an attempt is in flight, so the form can be disabled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormEnabled))]
    private bool isBusyOrChecking = true;

    [ObservableProperty]
    private string userName = string.Empty;

    [ObservableProperty]
    private string displayName = string.Empty;

    /// <summary>
    /// The line shown under the form: what went wrong, or what just happened. Success and failure are told
    /// apart by <see cref="IsMessageProblem"/> rather than by the caller inspecting the text.
    /// </summary>
    [ObservableProperty]
    private string message = string.Empty;

    [ObservableProperty]
    private bool isMessageProblem;

    /// <summary>True when the installation is already set up, so this is an ordinary sign-in.</summary>
    public bool IsSignInMode => !IsFirstRun;

    /// <summary>What the password rules are, shown while the first administrator is being created.</summary>
    public string PasswordRulesText => PasswordPolicy.Describe();

    public string HeadlineText => IsFirstRun
        ? "Set up MediStock"
        : "Sign in to MediStock";

    public string IntroText => IsFirstRun
        ? "This installation has no accounts yet. Create the administrator who will manage this pharmacy, then sign in."
        : "Enter the account name and password you were given. Every attempt is recorded in the audit trail.";

    public string ActionText => IsFirstRun
        ? "Create account and sign in"
        : "Sign in";

    public bool IsFormEnabled => !IsBusyOrChecking;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    /// <summary>Works out whether to offer first-run setup, and says so if the database cannot be asked.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusyOrChecking = true;
        try
        {
            IsFirstRun = !await _authentication.IsSetUpAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not tell whether this installation has any accounts.");
            ShowProblem("The account list could not be read. Check the database connection and try again.");
        }
        finally
        {
            IsBusyOrChecking = false;
        }
    }

    /// <summary>Signs somebody in. Returns true when the window should close.</summary>
    public async Task<bool> SignInAsync(string password, CancellationToken cancellationToken = default)
    {
        ClearMessage();

        if (string.IsNullOrWhiteSpace(UserName))
        {
            ShowProblem("Enter your account name.");
            return false;
        }

        if (string.IsNullOrEmpty(password))
        {
            ShowProblem("Enter your password.");
            return false;
        }

        IsBusyOrChecking = true;
        try
        {
            var result = await _authentication
                .SignInAsync(UserName.Trim(), password, cancellationToken)
                .ConfigureAwait(true);

            if (result.Succeeded)
            {
                _logger.LogInformation("Signed in as {Account}.", result.User!.UserName);
                return true;
            }

            ShowProblem(result.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signing in failed unexpectedly.");
            ShowProblem("Signing in could not be completed. See the application log for details.");
            return false;
        }
        finally
        {
            IsBusyOrChecking = false;
        }
    }

    /// <summary>
    /// Creates the first administrator and signs them in. Returns true when the window should close.
    /// </summary>
    public async Task<bool> SetUpAsync(string password, string confirmPassword, CancellationToken cancellationToken = default)
    {
        ClearMessage();

        var draft = new UserDraft
        {
            UserName = UserName.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? UserName.Trim() : DisplayName.Trim(),
            Role = Role.Administrator,
            Password = password,
            ConfirmPassword = confirmPassword,
            Reason = "First administrator created during setup.",
        };

        // Checked here rather than through the domain so the operator sees every problem at once, before
        // anything is written.
        var problems = draft.Validate();
        if (problems.Count > 0)
        {
            ShowProblem(string.Join(Environment.NewLine, problems));
            return false;
        }

        IsBusyOrChecking = true;
        try
        {
            var result = await _authentication
                .SetUpFirstAdministratorAsync(draft, cancellationToken)
                .ConfigureAwait(true);

            if (result.Succeeded)
            {
                _logger.LogInformation("First administrator created and signed in.");
                return true;
            }

            ShowProblem(result.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating the first administrator failed unexpectedly.");
            ShowProblem(OperationResult.From(ex).Message);
            return false;
        }
        finally
        {
            IsBusyOrChecking = false;
        }
    }

    private void ShowProblem(string text)
    {
        IsMessageProblem = true;
        Message = text;
    }

    private void ClearMessage()
    {
        IsMessageProblem = false;
        Message = string.Empty;
    }
}
