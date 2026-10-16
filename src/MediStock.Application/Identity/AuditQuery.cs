using MediStock.Domain.Identity;

namespace MediStock.Application.Identity;

/// <summary>How the audit trail is filtered. Every part is optional; the empty query means "everything".</summary>
public sealed record AuditQuery
{
    /// <summary>The most rows one search will return.</summary>
    public const int MaxResults = 5_000;

    /// <summary>Earliest instant to include, in local time. Null means no lower bound.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Latest instant to include, in local time. Null means no upper bound.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Only this action.</summary>
    public AuditAction? Action { get; init; }

    /// <summary>Only this account name.</summary>
    public string? Actor { get; init; }

    /// <summary>Only rows about this kind of record.</summary>
    public string? EntityType { get; init; }

    /// <summary>
    /// Free text matched against the summary, the reason, the detail, the entity identifier and the account
    /// name, so one box on screen can find a person, an invoice or a word.
    /// </summary>
    public string? Term { get; init; }

    /// <summary>True to show the newest rows first. Defaults to newest first, which is what a support call wants.</summary>
    public bool NewestFirst { get; init; } = true;

    public int Take { get; init; } = 500;

    /// <summary>
    /// The query with the loose ends tied up: the count clamped to what the screen may ask for, and the
    /// dates ordered. Throws when the range is back to front, because silently swapping them would hide a
    /// mistake in a filter the operator set.
    /// </summary>
    public AuditQuery Normalise()
    {
        if (From is { } from && To is { } to && from > to)
        {
            throw new ArgumentException("The start of the range is after the end of it.", nameof(From));
        }

        return this with
        {
            Take = Math.Clamp(Take <= 0 ? 500 : Take, 1, MaxResults),
            Term = string.IsNullOrWhiteSpace(Term) ? null : Term.Trim(),
            Actor = string.IsNullOrWhiteSpace(Actor) ? null : Actor.Trim(),
            EntityType = string.IsNullOrWhiteSpace(EntityType) ? null : EntityType.Trim(),
        };
    }
}
