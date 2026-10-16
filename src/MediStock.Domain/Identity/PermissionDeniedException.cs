using MediStock.Domain.Common;

namespace MediStock.Domain.Identity;

/// <summary>
/// Raised when a signed-in person asks for something their permissions do not cover. It derives from
/// <see cref="DomainException"/> so the application can turn it into an ordinary operator-visible
/// message without treating it as a crash.
/// </summary>
public sealed class PermissionDeniedException : DomainException
{
    public PermissionDeniedException(string message) : base(message)
    {
    }
}
