using MediStock.Domain.Identity;

namespace MediStock.Application.Identity;

/// <summary>
/// A person as the interface and the application need to see them: enough to greet them, to decide what
/// they may do, and to list them on a settings screen. The stored password is not part of this shape.
/// </summary>
public sealed record UserDto(
    int Id,
    string UserName,
    string DisplayName,
    Role Role,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastSignedInAtUtc)
{
    /// <summary>What to call this person on screen.</summary>
    public string FriendlyName => string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName;

    /// <summary>The role, written the way it appears on screen.</summary>
    public string RoleLabel => Role.Label();

    /// <summary>What this account may do, derived from its role.</summary>
    public PermissionSet Permissions => RoleCatalog.PermissionsFor(Role);

    /// <summary>True when the account may not sign in at present.</summary>
    public bool IsDeactivated => !IsActive;
}
