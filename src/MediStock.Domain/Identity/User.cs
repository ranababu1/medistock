using MediStock.Domain.Common;

namespace MediStock.Domain.Identity;

/// <summary>
/// A person who may use the application. Accounts are deactivated rather than deleted, so that every
/// audit row and every invoice keeps naming somebody real.
/// </summary>
public class User
{
    public const int UserNameMaxLength = 64;
    public const int DisplayNameMaxLength = 120;

    /// <summary>Required by Entity Framework Core.</summary>
    private User()
    {
    }

    private User(string userName, string displayName)
    {
        UserName = userName;
        NormalisedUserName = Guard.NormaliseForComparison(userName)!;
        DisplayName = displayName;
    }

    public int Id { get; private set; }

    /// <summary>The name typed at the sign-in prompt, kept as it was typed for display.</summary>
    public string UserName { get; private set; } = string.Empty;

    /// <summary>
    /// The name folded to lower case, which is what the unique index is on, so that "Imran" and "imran"
    /// cannot both be created.
    /// </summary>
    public string NormalisedUserName { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public Role Role { get; private set; }

    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// The stored password, in the encoded form <see cref="PasswordHash"/> writes. Read through
    /// <see cref="Password"/> rather than directly, so nothing in the application handles the encoded
    /// string by accident.
    /// </summary>
    public string PasswordHashEncoded { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>When the account last signed in successfully, or null if it never has.</summary>
    public DateTimeOffset? LastSignedInAtUtc { get; private set; }

    /// <summary>What this account may do, derived from its role.</summary>
    public PermissionSet Permissions => RoleCatalog.PermissionsFor(Role);

    /// <summary>The stored password as a value that can check a candidate.</summary>
    public PasswordHash Password => PasswordHash.Parse(PasswordHashEncoded);

    public static User Create(
        string userName,
        string displayName,
        Role role,
        PasswordHash password,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (!Enum.IsDefined(role))
        {
            throw new DomainValidationException($"'{role}' is not a known role.");
        }

        var timestamp = nowUtc ?? DateTimeOffset.UtcNow;

        return new User(
            Guard.Required(userName, UserNameMaxLength, "Account name"),
            Guard.Required(displayName, DisplayNameMaxLength, "Display name"))
        {
            Role = role,
            PasswordHashEncoded = password.Encoded,
            IsActive = true,
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp,
        };
    }

    /// <summary>Returns true when <paramref name="permission"/> is granted to this account's role.</summary>
    public bool Can(Permission permission) => Permissions.Has(permission);

    /// <summary>
    /// Checks a password against this account, in constant time, including when the account is inactive
    /// or its stored record is damaged. Answers only the question "is this the password", so a caller
    /// deciding whether to sign somebody in must also check <see cref="IsActive"/> — and can then show
    /// the same message either way, which is what stops the sign-in prompt from telling a stranger which
    /// account names exist.
    /// </summary>
    public bool MatchesPassword(string? password)
        => PasswordHash.TryParse(PasswordHashEncoded, out var stored)
            && stored!.Matches(password);

    /// <summary>Renames the account. Uniqueness is the directory's job; this only checks the shape.</summary>
    public void Rename(string userName, DateTimeOffset? nowUtc = null)
    {
        UserName = Guard.Required(userName, UserNameMaxLength, "Account name");
        NormalisedUserName = Guard.NormaliseForComparison(UserName)!;
        Touch(nowUtc);
    }

    public void SetDisplayName(string displayName, DateTimeOffset? nowUtc = null)
    {
        DisplayName = Guard.Required(displayName, DisplayNameMaxLength, "Display name");
        Touch(nowUtc);
    }

    /// <summary>
    /// Replaces the stored password. The plaintext was validated and hashed by the caller; this only
    /// records the result.
    /// </summary>
    public void SetPassword(PasswordHash password, DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        PasswordHashEncoded = password.Encoded;
        Touch(nowUtc);
    }

    /// <summary>
    /// Moves the account to another role.
    /// </summary>
    /// <param name="role">The new role.</param>
    /// <param name="otherActiveAdministrators">
    /// How many <em>other</em> active accounts hold <see cref="Role.Administrator"/>. An installation
    /// whose only administrator is demoted cannot manage its own users again, so it is refused.
    /// </param>
    public void ChangeRole(Role role, int otherActiveAdministrators, DateTimeOffset? nowUtc = null)
    {
        if (!Enum.IsDefined(role))
        {
            throw new DomainValidationException($"'{role}' is not a known role.");
        }

        if (role == Role)
        {
            return;
        }

        if (Role == Role.Administrator && role != Role.Administrator && otherActiveAdministrators <= 0)
        {
            throw new DomainValidationException(
                "This is the only active administrator. Give somebody else the administrator role first, "
                + "otherwise nobody could manage users or take a backup.");
        }

        Role = role;
        Touch(nowUtc);
    }

    /// <summary>
    /// Stops the account signing in. Historical records keep pointing at it.
    /// </summary>
    /// <param name="otherActiveAdministrators">
    /// How many <em>other</em> active accounts hold the administrator role. See
    /// <see cref="ChangeRole"/> for why the last one is refused.
    /// </param>
    public void Deactivate(int otherActiveAdministrators, DateTimeOffset? nowUtc = null)
    {
        if (!IsActive)
        {
            return;
        }

        if (Role == Role.Administrator && otherActiveAdministrators <= 0)
        {
            throw new DomainValidationException(
                "This is the only active administrator. Give somebody else the administrator role first, "
                + "otherwise nobody could manage users or take a backup.");
        }

        IsActive = false;
        Touch(nowUtc);
    }

    public void Reactivate(DateTimeOffset? nowUtc = null)
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        Touch(nowUtc);
    }

    /// <summary>Notes a successful sign-in, so a settings screen can show when an account was last used.</summary>
    public void RecordSignIn(DateTimeOffset? nowUtc = null)
    {
        var timestamp = nowUtc ?? DateTimeOffset.UtcNow;
        LastSignedInAtUtc = timestamp;
        UpdatedAtUtc = timestamp;
    }

    /// <summary>
    /// What the operator is called on screen: their display name, falling back to the account name so a
    /// half-set-up account still has something to show.
    /// </summary>
    public string FriendlyName => string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName;

    private void Touch(DateTimeOffset? nowUtc) => UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
}
