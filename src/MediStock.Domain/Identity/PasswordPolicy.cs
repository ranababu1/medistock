using MediStock.Domain.Common;

namespace MediStock.Domain.Identity;

/// <summary>
/// The rules a password must satisfy before it is stored. Deliberately short and stated in plain
/// language: a pharmacy counter is a shared, busy place, and a rule an operator cannot remember is a
/// rule that ends up written on a sticky note.
/// </summary>
public static class PasswordPolicy
{
    /// <summary>Shortest password accepted.</summary>
    public const int MinimumLength = 10;

    /// <summary>
    /// Longest password accepted. Capped rather than unlimited, because the cost of checking a password
    /// grows with its length and an attacker who can post a megabyte should not be able to turn that
    /// into work for the till.
    /// </summary>
    public const int MaximumLength = 128;

    /// <summary>
    /// The handful of passwords that would be tried first against software that manages medicines.
    /// A full blocklist is not the point; refusing the obvious ones is.
    /// </summary>
    private static readonly string[] TooCommon =
    [
        "password",
        "password1",
        "password123",
        "passw0rd",
        "letmein",
        "qwerty",
        "qwertyuiop",
        "1234567890",
        "iloveyou",
        "welcome",
        "changeme",
        "administrator",
        "medistock",
        "pharmacy",
        "medicine",
        "letmein123",
    ];

    /// <summary>
    /// Everything wrong with <paramref name="password"/>, in the order an operator should fix it. Empty
    /// when the password may be used.
    /// </summary>
    /// <param name="password">The candidate.</param>
    /// <param name="userName">
    /// The account this password is for, when it is known. A password containing the account name is
    /// refused, because that is the first thing anyone guessing will try.
    /// </param>
    public static IReadOnlyList<string> Validate(string? password, string? userName = null)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(password))
        {
            problems.Add("A password is required.");
            return problems;
        }

        if (password.Length < MinimumLength)
        {
            problems.Add($"The password must be at least {MinimumLength} characters long.");
        }

        if (password.Length > MaximumLength)
        {
            problems.Add($"The password must be {MaximumLength} characters or fewer.");
        }

        if (password.Distinct().Count() < 4)
        {
            problems.Add("The password must use at least four different characters.");
        }

        if (TooCommon.Contains(password, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add("That password is too easy to guess. Choose something else.");
        }

        if (!string.IsNullOrWhiteSpace(userName))
        {
            var name = userName.Trim();
            if (name.Length >= 3 && password.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("The password must not contain the account name.");
            }
        }

        return problems;
    }

    /// <summary>Throws when the password may not be used. Used where a return value would be easy to ignore.</summary>
    public static void Ensure(string? password, string? userName = null)
    {
        var problems = Validate(password, userName);
        if (problems.Count > 0)
        {
            throw new DomainValidationException(string.Join(Environment.NewLine, problems));
        }
    }

    /// <summary>
    /// A sentence an administrator can read out, describing what a password has to be.
    /// </summary>
    public static string Describe() =>
        $"At least {MinimumLength} characters, at most {MaximumLength}, four different characters or more, "
        + "not one of the obvious passwords, and not containing the account name.";
}
