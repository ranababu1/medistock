using MediStock.Domain.Identity;

namespace MediStock.Application.Identity;

/// <summary>One row of the audit trail, as the diagnostics screen shows it.</summary>
public sealed record AuditEntryDto(
    long Id,
    DateTimeOffset OccurredAtUtc,
    AuditAction Action,
    string Actor,
    int? UserId,
    string Summary,
    string? EntityType,
    string? EntityId,
    string? Reason,
    string? Detail)
{
    /// <summary>The action written the way it appears on screen.</summary>
    public string ActionLabel => AuditText.Label(Action);

    /// <summary>What the row is about, on one line, for a list that has no room for columns.</summary>
    public string Describe()
    {
        var about = string.IsNullOrWhiteSpace(EntityId) ? null : $" ({EntityType} {EntityId})";
        return $"{Summary}{about}";
    }

    /// <summary>
    /// The same one-liner as <see cref="Describe"/>, as a property, because the detail panel on the settings
    /// screen binds to it and XAML cannot call a method.
    /// </summary>
    public string Description => Describe();
}
