using MediStock.Application.Common;

namespace MediStock.Application.Identity;

/// <summary>Why a sign-in was refused, from the application's point of view rather than the database's.</summary>
public enum SignInFailure
{
    /// <summary>The name and password did not match an active account.</summary>
    BadCredentials,

    /// <summary>The account exists but has been deactivated.</summary>
    AccountDeactivated,

    /// <summary>This installation has no accounts yet, so there is nobody to sign in as.</summary>
    NotSetUp,
}

/// <summary>
/// The outcome of a sign-in attempt. It carries a message the sign-in screen can show as-is, and a reason
/// the caller may act on — but the screen shows the same wording for every kind of bad credential, so that
/// a stranger cannot use the prompt to find out which account names exist.
/// </summary>
public sealed record SignInResult(bool Succeeded, SignInFailure? Failure, string Message, UserDto? User)
{
    public static SignInResult Success(UserDto user)
        => new(true, null, $"Signed in as {user.FriendlyName}.", user);

    public static SignInResult Refused(SignInFailure failure, string message)
        => new(false, failure, message, null);
}

/// <summary>
/// Signing in, signing out, setting up the first administrator, and changing your own password.
/// </summary>
/// <remarks>
/// The wording of a refusal lives here, in one place, so that every path that refuses a sign-in refuses it
/// in the same words.
/// </remarks>
public interface IAuthenticationService
{
    /// <summary>
    /// The message shown whenever a name and password do not match an active account — whether the account
    /// is unknown, the password is wrong, or the account has been deactivated.
    /// </summary>
    const string BadCredentialsMessage =
        "That account name and password do not match an active account. Check both and try again.";

    /// <summary>True when at least one account exists, active or not.</summary>
    Task<bool> IsSetUpAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the first administrator on a fresh installation, and signs them in. Refused when any account
    /// already exists, so it cannot be used to add an administrator to a working installation.
    /// </summary>
    Task<OperationResult<UserDto>> SetUpFirstAdministratorAsync(UserDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a name and password and, when they are good, makes that person the signed-in user for the
    /// rest of the session. Every attempt is audited, successful or not.
    /// </summary>
    Task<SignInResult> SignInAsync(string userName, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets the signed-in person. Audited, because knowing when a till was left unattended matters.
    /// </summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lets the signed-in person choose a new password, which requires proving they know the old one. The
    /// session survives the change so the operator is not thrown back to the sign-in prompt.
    /// </summary>
    Task<OperationResult> ChangeOwnPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);
}
