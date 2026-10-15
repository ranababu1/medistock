namespace MediStock.Domain.Common;

/// <summary>
/// Base type for business rule violations that are expected to be handled and shown to the
/// operator. Anything derived from this must carry a message that is safe to display.
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message) : base(message)
    {
    }
}

/// <summary>Raised when input or state violates a domain invariant.</summary>
public sealed class DomainValidationException : DomainException
{
    public DomainValidationException(string message) : base(message)
    {
    }
}
