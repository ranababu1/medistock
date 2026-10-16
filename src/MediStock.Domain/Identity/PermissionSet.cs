using System.Collections.Frozen;

namespace MediStock.Domain.Identity;

/// <summary>
/// The permissions one person holds, as an immutable set that always answers the same way. Holding the
/// set rather than the role means a screen can ask what the signed-in person may do without knowing
/// which role granted it.
/// </summary>
public sealed class PermissionSet
{
    private static readonly PermissionSet Nothing = new(FrozenSet<Permission>.Empty);

    private readonly FrozenSet<Permission> _permissions;

    private PermissionSet(FrozenSet<Permission> permissions) => _permissions = permissions;

    /// <summary>A set that grants nothing. What the application holds before anyone has signed in.</summary>
    public static PermissionSet Empty => Nothing;

    /// <summary>Every permission the application defines.</summary>
    public static PermissionSet All => new(Enum.GetValues<Permission>().ToFrozenSet());

    /// <summary>The permissions in the set, in the order they are declared.</summary>
    public IReadOnlyList<Permission> Permissions => [.. _permissions.Order()];

    public int Count => _permissions.Count;

    public static PermissionSet Of(IEnumerable<Permission> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var set = permissions.ToFrozenSet();
        return set.Count == 0 ? Nothing : new PermissionSet(set);
    }

    /// <summary>True when <paramref name="permission"/> is granted.</summary>
    public bool Has(Permission permission) => _permissions.Contains(permission);

    /// <summary>True when every one of <paramref name="permissions"/> is granted.</summary>
    public bool HasAll(params Permission[] permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return permissions.All(Has);
    }

    /// <summary>
    /// Throws unless the permission is granted. This is the one place a refusal is worded, so every
    /// refusal in the application reads the same way to an operator.
    /// </summary>
    /// <param name="permission">The permission the action requires.</param>
    /// <param name="what">
    /// The action in the operator's own words, for example "take this return". Left out when the
    /// permission label already says it.
    /// </param>
    public void Require(Permission permission, string? what = null)
    {
        if (Has(permission))
        {
            return;
        }

        throw new PermissionDeniedException(Explain(permission, what));
    }

    /// <summary>A readable list of what the set grants, for a settings screen.</summary>
    public string Describe() => _permissions.Count == 0
        ? "nothing"
        : string.Join(", ", Permissions.Select(p => p.Label().ToLowerInvariant()));

    /// <summary>
    /// The message shown when an action is refused. It names the action and the permission code, and
    /// tells the operator who can fix it — the role they hold is deliberately not mentioned, because
    /// knowing their role would not help them get the work done.
    /// </summary>
    public static string Explain(Permission permission, string? what = null)
    {
        var action = string.IsNullOrWhiteSpace(what)
            ? permission.Label().ToLowerInvariant()
            : what.Trim();

        return $"You do not have permission to {action}. "
            + $"An administrator can grant it (permission: {permission}).";
    }
}
