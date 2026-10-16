using MediStock.Domain.Identity;

namespace MediStock.Application.Identity;

/// <summary>
/// Turns a plaintext password into the value that gets stored, and nothing else. Hashing lives behind an
/// interface so the work factor is a configuration choice rather than a constant baked into the domain,
/// and so a test can hash cheaply without weakening what the released application does.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Derives a storable hash with a fresh random salt.</summary>
    PasswordHash Hash(string password);

    /// <summary>
    /// True when <paramref name="password"/> is the password behind <paramref name="stored"/>. Returns
    /// false, rather than throwing, when the stored value cannot be read — a damaged record must not be
    /// able to do anything worse than refuse a sign-in.
    /// </summary>
    bool Verify(string? password, string? stored);

    /// <summary>
    /// The work factor new passwords are written with, so a settings screen can state it and a test can
    /// assert the released figure.
    /// </summary>
    int Iterations { get; }
}
