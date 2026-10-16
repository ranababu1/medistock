using MediStock.Application.Common;
using MediStock.Domain.Common;
using MediStock.Domain.Identity;

namespace MediStock.Application.Identity;

/// <summary>
/// The person using the application right now, and therefore what it is allowed to do. Read by every screen
/// that shows or hides anything, and by the catalogues that refuse an action.
/// </summary>
/// <remarks>
/// This answers <em>who is signed in</em>. It is not a security boundary on its own: the same checks are
/// applied in the application layer, and the records that matter are protected by the database. See
/// <c>docs/decisions/0009-users-roles-and-the-audit-trail.md</c>.
/// </remarks>
public interface IUserSession
{
    /// <summary>
    /// Raised after the signed-in person changes, in either direction. The shell listens so it can notice a
    /// sign-out it did not initiate and return to the sign-in window.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>The signed-in person, or null before a sign-in and after a sign-out.</summary>
    UserDto? CurrentUser { get; }

    /// <summary>True once somebody has signed in.</summary>
    bool IsSignedIn { get; }

    /// <summary>What the signed-in person may do. Grants nothing when nobody is signed in.</summary>
    PermissionSet Permissions { get; }

    /// <summary>True when the action is allowed. Used by a screen to hide a button rather than to refuse it.</summary>
    bool Can(Permission permission);

    /// <summary>
    /// Throws <see cref="PermissionDeniedException"/> unless the action is allowed. Used by the
    /// application layer so a refusal cannot be bypassed by a screen that forgot to hide a button.
    /// </summary>
    /// <param name="permission">The permission the action requires.</param>
    /// <param name="what">
    /// The action in the operator's words, for example "cancel this invoice". A catalogue passes this so
    /// the message reads as a sentence rather than naming the permission twice.
    /// </param>
    void Require(Permission permission, string? what = null);

    /// <summary>True when the signed-in person holds the administrator role. A convenience, not a rule.</summary>
    bool IsAdministrator { get; }
}

/// <summary>
/// The mutable form of <see cref="IUserSession"/>, written by the authentication service when somebody
/// signs in or out.
/// </summary>
/// <remarks>
/// Registered as a singleton and injected as the concrete type only into the authentication service —
/// everything else takes <see cref="IUserSession"/>, so there is exactly one place in the application
/// that can decide who is signed in.
/// </remarks>
public sealed class UserSession : IUserSession
{
    private UserDto? _current;

    /// <inheritdoc />
    public event EventHandler? Changed;

    public UserDto? CurrentUser => _current;

    public bool IsSignedIn => _current is not null;

    public PermissionSet Permissions => _current?.Permissions ?? PermissionSet.Empty;

    public bool IsAdministrator => _current?.Role == Role.Administrator;

    public bool Can(Permission permission) => Permissions.Has(permission);

    public void Require(Permission permission, string? what = null) => Permissions.Require(permission, what);

    /// <summary>Records who just signed in, and tells anyone listening.</summary>
    public void Adopt(UserDto user)
    {
        ArgumentNullException.ThrowIfNull(user);
        _current = user;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the signed-in person, returning the application to granting nothing.</summary>
    public void Clear()
    {
        if (_current is null)
        {
            return;
        }

        _current = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replaces the cached details of the signed-in person, after they changed their own password or an
    /// administrator renamed them.
    /// </summary>
    public void Refresh(UserDto user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (_current?.Id != user.Id)
        {
            return;
        }

        if (_current == user)
        {
            return;
        }

        _current = user;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
