using MediStock.Application.Common;
using MediStock.Domain.Identity;

namespace MediStock.Application.Identity;

/// <summary>How accounts are looked up and listed on the users screen.</summary>
public sealed record UserQuery
{
    /// <summary>The most accounts one search will return.</summary>
    public const int MaxResults = 500;

    /// <summary>Matched against the account name and the display name.</summary>
    public string? Term { get; init; }

    /// <summary>When true, deactivated accounts are listed as well.</summary>
    public bool IncludeInactive { get; init; }

    public int Take { get; init; } = 200;

    public bool IsEmpty => string.IsNullOrWhiteSpace(Term) && !IncludeInactive;
}

/// <summary>
/// The accounts on this installation: who exists, and the administrative changes an administrator may
/// make to them.
/// </summary>
/// <remarks>
/// Every method that changes an account requires <see cref="Permission.ManageUsers"/> and writes an audit
/// row. Reading the list requires it too: knowing who works here is itself administrative information.
/// </remarks>
public interface IUserDirectory
{
    /// <summary>Accounts matching <paramref name="query"/>, ordered by display name.</summary>
    Task<IReadOnlyList<UserDto>> SearchAsync(UserQuery query, CancellationToken cancellationToken = default);

    /// <summary>One account, or null when there is no such account.</summary>
    Task<UserDto?> FindAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>How many accounts exist, active or not. Zero means this installation has not been set up yet.</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an account. Refused when the name is already taken, ignoring case, so that "Imran" and
    /// "imran" cannot both exist and be confused for one another.
    /// </summary>
    Task<OperationResult<UserDto>> CreateAsync(UserDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an account to another role. Refused when it would leave the installation with no active
    /// administrator.
    /// </summary>
    Task<OperationResult<UserDto>> SetRoleAsync(int userId, Role role, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops or allows an account to sign in. Refused when it would leave the installation with no active
    /// administrator.
    /// </summary>
    Task<OperationResult<UserDto>> SetActiveAsync(int userId, bool isActive, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets somebody else's password. The operator does not have to know the old one, which is why this is
    /// audited separately from <see cref="IAuthenticationService.ChangeOwnPasswordAsync"/>.
    /// </summary>
    Task<OperationResult> ResetPasswordAsync(int userId, string newPassword, string? reason = null, CancellationToken cancellationToken = default);
}
