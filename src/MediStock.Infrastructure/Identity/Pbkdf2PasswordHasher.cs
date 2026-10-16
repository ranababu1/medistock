using MediStock.Application.Identity;
using MediStock.Domain.Identity;

namespace MediStock.Infrastructure.Identity;

/// <summary>
/// The application's password hashing: PBKDF2-HMAC-SHA256, with the iteration count a setting rather than a
/// constant.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm and the encoding are the domain's — see <see cref="PasswordHash"/> — and this type only
/// supplies the work factor, which is the one part that belongs to the deployment. A released application
/// uses <see cref="PasswordHash.RecommendedIterations"/>; an integration test that creates a hundred
/// accounts uses the domain's floor, because the point of those tests is the schema and the rules around
/// them and not the cost of the hash.
/// </para>
/// <para>
/// The work factor is not a secret and is written into every stored hash, so raising it later needs no
/// migration: new passwords are written at the new cost and old ones are re-hashed on the next successful
/// sign-in, which is why <see cref="PasswordHash.NeedsRehash"/> exists.
/// </para>
/// </remarks>
internal sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private readonly int _iterations;

    public Pbkdf2PasswordHasher(int iterations = PasswordHash.RecommendedIterations)
    {
        if (iterations < PasswordHash.MinimumIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                iterations,
                $"The work factor must be at least {PasswordHash.MinimumIterations}.");
        }

        _iterations = iterations;
    }

    public int Iterations => _iterations;

    public PasswordHash Hash(string password) => PasswordHash.Create(password, _iterations);

    /// <summary>
    /// Checks a password against a stored value. A stored value that cannot be read is a refusal rather than
    /// an error: a damaged row must not be able to do anything beyond keeping its owner out, and it must not
    /// crash the sign-in screen either.
    /// </summary>
    public bool Verify(string? password, string? stored)
        => PasswordHash.TryParse(stored, out var hash) && hash!.Matches(password);
}
