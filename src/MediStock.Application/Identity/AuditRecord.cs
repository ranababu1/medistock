using MediStock.Domain.Identity;

namespace MediStock.Application.Identity;

/// <summary>
/// Something worth recording, in the shape the caller has it. The actor defaults to whoever is signed in;
/// a caller only names one when there is nobody signed in, which is the refused sign-in case.
/// </summary>
public sealed record AuditRecord(AuditAction Action, string Summary)
{
    /// <summary>The kind of record this was about, for example <c>Medicine</c> or <c>Sale</c>.</summary>
    public string? EntityType { get; init; }

    /// <summary>The identifier of that record, as text, so an invoice number fits as well as an id.</summary>
    public string? EntityId { get; init; }

    /// <summary>Why, where the pharmacy requires a reason for the action.</summary>
    public string? Reason { get; init; }

    /// <summary>Anything else worth keeping, such as the values that changed.</summary>
    public string? Detail { get; init; }

    /// <summary>The account name to record. Left out to use the signed-in person, or <c>system</c>.</summary>
    public string? Actor { get; init; }

    /// <summary>The account identifier to record. Left out for the same reason as <see cref="Actor"/>.</summary>
    public int? UserId { get; init; }
}
