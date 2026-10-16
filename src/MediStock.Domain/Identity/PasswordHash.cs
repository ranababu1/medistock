using System.Security.Cryptography;
using System.Text;
using MediStock.Domain.Common;

namespace MediStock.Domain.Identity;

/// <summary>
/// A password as it is stored: a salt, a work factor and the derived key, kept together in one string
/// so the record carries everything needed to check a password later.
/// </summary>
/// <remarks>
/// <para>
/// The plaintext password is never stored, never logged and never leaves this method. What is stored is
/// PBKDF2-HMAC-SHA256, written as <c>pbkdf2-sha256$iterations$salt$key</c> with the two binary parts
/// Base64 encoded.
/// </para>
/// <para>
/// PBKDF2 is used because it is in the base class library, so the application does not take a
/// dependency on a password library to do the one thing it needs. The iteration count is stored with
/// each hash rather than assumed, so raising <see cref="RecommendedIterations"/> in a later release
/// still verifies the passwords written by this one; <see cref="NeedsRehash"/> says which of the stored
/// hashes would be worth writing again at the new cost.
/// </para>
/// </remarks>
public sealed class PasswordHash : IEquatable<PasswordHash>
{
    /// <summary>The only algorithm this class writes, and the only one it will read back.</summary>
    public const string AlgorithmName = "pbkdf2-sha256";

    /// <summary>
    /// The work factor used for new passwords. 210,000 is the OWASP guidance for PBKDF2-HMAC-SHA256 and
    /// costs a fraction of a second on the machines this application runs on, which is unnoticeable at
    /// a sign-in prompt and expensive across a stolen copy of the database.
    /// </summary>
    public const int RecommendedIterations = 210_000;

    /// <summary>
    /// The lowest work factor that will be read back. A stored hash below this is treated as
    /// unparseable rather than accepted, so a record edited by hand cannot quietly weaken the store.
    /// </summary>
    public const int MinimumIterations = 10_000;

    /// <summary>Length of the random salt, in bytes. Sixteen bytes is the usual recommendation.</summary>
    public const int SaltBytes = 16;

    /// <summary>Length of the derived key, in bytes.</summary>
    public const int KeyBytes = 32;

    /// <summary>The longest encoded form accepted, so a corrupt or hostile record cannot be huge.</summary>
    public const int EncodedMaxLength = 512;

    private readonly byte[] _salt;
    private readonly byte[] _key;

    private PasswordHash(string encoded, int iterations, byte[] salt, byte[] key)
    {
        Encoded = encoded;
        Iterations = iterations;
        _salt = salt;
        _key = key;
    }

    /// <summary>The stored form: algorithm, work factor, salt and key.</summary>
    public string Encoded { get; }

    /// <summary>The work factor this hash was written with.</summary>
    public int Iterations { get; }

    /// <summary>True when this hash was written at a lower cost than the application now recommends.</summary>
    public bool NeedsRehash => Iterations < RecommendedIterations;

    /// <summary>
    /// Derives a new hash for <paramref name="password"/> with a fresh random salt.
    /// </summary>
    /// <param name="password">
    /// The plaintext. It is not validated for strength here — that is <see cref="PasswordPolicy"/>'s
    /// job, so an existing weak password can still be re-hashed.
    /// </param>
    /// <param name="iterations">The work factor. Left out to use <see cref="RecommendedIterations"/>.</param>
    public static PasswordHash Create(string password, int iterations = RecommendedIterations)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new DomainValidationException("A password is required.");
        }

        if (iterations < MinimumIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                iterations,
                $"The work factor must be at least {MinimumIterations}.");
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Derive(password, salt, iterations);

        return new PasswordHash(Encode(iterations, salt, key), iterations, salt, key);
    }

    /// <summary>
    /// Reads a stored hash back. Returns false instead of throwing, so a caller deciding whether to
    /// grant a session can treat a damaged record as simply "not this password".
    /// </summary>
    public static bool TryParse(string? encoded, out PasswordHash? hash)
    {
        hash = null;

        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > EncodedMaxLength)
        {
            return false;
        }

        var parts = encoded.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], AlgorithmName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var iterations) || iterations < MinimumIterations)
        {
            return false;
        }

        byte[] salt;
        byte[] key;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            key = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || key.Length == 0)
        {
            return false;
        }

        hash = new PasswordHash(encoded, iterations, salt, key);
        return true;
    }

    /// <summary>Reads a stored hash back, throwing when the record cannot be trusted.</summary>
    public static PasswordHash Parse(string? encoded) => TryParse(encoded, out var hash)
        ? hash!
        : throw new DomainValidationException("The stored password is not in a recognised format.");

    /// <summary>
    /// Checks a password against this hash in an amount of time that does not depend on how much of the
    /// password was right.
    /// </summary>
    /// <remarks>
    /// Whether the account is active is not part of this answer — see <see cref="User.MatchesPassword"/>.
    /// </remarks>
    public bool Matches(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        var candidate = Derive(password, _salt, Iterations);
        return CryptographicOperations.FixedTimeEquals(candidate, _key);
    }

    public bool Equals(PasswordHash? other) => other is not null
        && string.Equals(Encoded, other.Encoded, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as PasswordHash);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Encoded);

    /// <summary>
    /// The stored form. Deliberately the only way out: the salt and key are not exposed as separate
    /// properties, so nothing in the application is tempted to log or compare them piecemeal.
    /// </summary>
    public override string ToString() => Encoded;

    private static byte[] Derive(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);

    private static string Encode(int iterations, byte[] salt, byte[] key)
        => $"{AlgorithmName}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
}
