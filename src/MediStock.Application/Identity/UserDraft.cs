using MediStock.Domain.Common;
using MediStock.Domain.Identity;

namespace MediStock.Application.Identity;

/// <summary>
/// The values an administrator supplies when creating an account, or when editing one. The password is
/// carried in the clear only long enough to be hashed; it is never stored, logged or audited.
/// </summary>
public sealed record UserDraft
{
    public static UserDraft Empty { get; } = new();

    public string UserName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public Role Role { get; init; } = Role.Cashier;

    /// <summary>The chosen password, in the clear. Required when creating; left out when editing.</summary>
    public string? Password { get; init; }

    /// <summary>Confirmation typed a second time, checked here so a typo cannot lock somebody out.</summary>
    public string? ConfirmPassword { get; init; }

    /// <summary>Why the account was created or changed. Recorded on the audit row.</summary>
    public string? Reason { get; init; }

    /// <summary>True when a password is being set as part of this draft.</summary>
    public bool HasPassword => !string.IsNullOrEmpty(Password);

    /// <summary>
    /// Whether the password must be supplied: it must when creating an account, and need not when only
    /// the name or the role is changing.
    /// </summary>
    public bool RequiresPassword { get; init; } = true;

    public static UserDraft FromDto(UserDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new UserDraft
        {
            UserName = dto.UserName,
            DisplayName = dto.DisplayName,
            Role = dto.Role,
            RequiresPassword = false,
        };
    }

    /// <summary>
    /// Every problem with the draft, in the order the operator should fix them, so the editor can show
    /// them all at once rather than one per attempt.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(UserName))
        {
            problems.Add("An account name is required.");
        }
        else if (UserName.Trim().Length > User.UserNameMaxLength)
        {
            problems.Add($"The account name must be {User.UserNameMaxLength} characters or fewer.");
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            problems.Add("A display name is required.");
        }
        else if (DisplayName.Trim().Length > User.DisplayNameMaxLength)
        {
            problems.Add($"The display name must be {User.DisplayNameMaxLength} characters or fewer.");
        }

        if (!Enum.IsDefined(Role))
        {
            problems.Add("Choose a role.");
        }

        if (RequiresPassword && !HasPassword && string.IsNullOrEmpty(ConfirmPassword))
        {
            problems.Add("A password is required for a new account.");
        }

        if (HasPassword)
        {
            if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
            {
                problems.Add("The two passwords do not match.");
            }

            problems.AddRange(PasswordPolicy.Validate(Password, UserName));
        }

        return problems;
    }

    /// <summary>
    /// Checks the draft and hashes the password, so the caller never handles the plaintext beyond this
    /// call. Throws <see cref="DomainValidationException"/> listing everything wrong.
    /// </summary>
    public PasswordHash HashPassword(IPasswordHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);

        var problems = Validate();
        if (problems.Count > 0)
        {
            throw new DomainValidationException(string.Join(Environment.NewLine, problems));
        }

        return hasher.Hash(Password!);
    }
}
