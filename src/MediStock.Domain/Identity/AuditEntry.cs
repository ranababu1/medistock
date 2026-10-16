using MediStock.Domain.Common;

namespace MediStock.Domain.Identity;

/// <summary>
/// One thing that happened, who did it and when. Rows are only ever added; nothing in the application
/// updates or deletes one, which is what makes the trail trustworthy.
/// </summary>
/// <remarks>
/// The constructor scrubs the free-text parts, so a caller cannot put a credential into the trail by
/// accident — for instance by logging a connection string inside an error message.
/// </remarks>
public class AuditEntry
{
    public const int ActorMaxLength = 120;
    public const int SummaryMaxLength = 400;
    public const int ReasonMaxLength = 400;
    public const int EntityTypeMaxLength = 60;
    public const int EntityIdMaxLength = 60;
    public const int DetailMaxLength = 2000;

    /// <summary>The account name recorded when nobody was signed in, for a refused sign-in.</summary>
    public const string SystemActor = "system";

    /// <summary>Required by Entity Framework Core. Kept private; see <see cref="Record"/>.</summary>
    private AuditEntry()
    {
    }

    private AuditEntry(AuditAction action, string actor, string summary, DateTimeOffset occurredAtUtc)
    {
        Action = action;
        Actor = actor;
        Summary = summary;
        OccurredAtUtc = occurredAtUtc;
    }

    public long Id { get; private set; }

    public AuditAction Action { get; private set; }

    /// <summary>
    /// The account name of whoever acted, or <see cref="SystemActor"/> when nobody was signed in. Stored
    /// as text as well as an identifier so a row still reads correctly after an account is renamed.
    /// </summary>
    public string Actor { get; private set; } = string.Empty;

    /// <summary>The account that acted, when there was one. Kept alongside the name rather than instead of it.</summary>
    public int? UserId { get; private set; }

    /// <summary>One line saying what happened, already safe to show.</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>The kind of record the action was about, for example <c>Medicine</c> or <c>Sale</c>.</summary>
    public string? EntityType { get; private set; }

    /// <summary>
    /// The identifier of the record, as text, because the identifier is sometimes a number and
    /// sometimes a document number such as an invoice.
    /// </summary>
    public string? EntityId { get; private set; }

    /// <summary>Why, when the pharmacy makes a reason mandatory for the action.</summary>
    public string? Reason { get; private set; }

    /// <summary>Anything else worth keeping, such as the values that changed.</summary>
    public string? Detail { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>True when this action is important enough that a support call is likely to ask about it.</summary>
    public bool IsSensitive => Action switch
    {
        AuditAction.PriceChanged
            or AuditAction.SaleCancelled
            or AuditAction.PurchaseReversed
            or AuditAction.ReturnRecorded
            or AuditAction.RestoreCompleted
            or AuditAction.UserRoleChanged => true,
        _ => false,
    };

    /// <summary>
    /// Records something that happened. The free-text parts are scrubbed of anything shaped like a
    /// credential and trimmed to the column widths before they are stored.
    /// </summary>
    /// <param name="action">What happened.</param>
    /// <param name="summary">One line saying what happened, in the operator's words.</param>
    /// <param name="actor">
    /// The account name of whoever acted. Defaults to <see cref="SystemActor"/>, which is what a refused
    /// sign-in records, because at that point there is no account to name.
    /// </param>
    /// <param name="userId">The account identifier, when there is one.</param>
    /// <param name="entityType">The kind of record this was about.</param>
    /// <param name="entityId">The identifier of that record.</param>
    /// <param name="reason">Why, where the pharmacy requires a reason.</param>
    /// <param name="detail">Anything else worth keeping.</param>
    /// <param name="nowUtc">The instant to record. Left out to use the current instant.</param>
    public static AuditEntry Record(
        AuditAction action,
        string summary,
        string? actor = null,
        int? userId = null,
        string? entityType = null,
        string? entityId = null,
        string? reason = null,
        string? detail = null,
        DateTimeOffset? nowUtc = null)
    {
        var name = string.IsNullOrWhiteSpace(actor) ? SystemActor : actor.Trim();

        return new AuditEntry(
            action,
            SensitiveText.Truncate(name, ActorMaxLength)!,
            Guard.Required(
                SensitiveText.Truncate(SensitiveText.Scrub(summary), SummaryMaxLength),
                SummaryMaxLength,
                "Audit summary"),
            nowUtc ?? DateTimeOffset.UtcNow)
        {
            UserId = userId,
            EntityType = Clean(entityType, EntityTypeMaxLength, "Entity type"),
            EntityId = Clean(entityId, EntityIdMaxLength, "Entity identifier"),
            Reason = Clean(reason, ReasonMaxLength, "Reason"),
            Detail = Clean(detail, DetailMaxLength, "Detail"),
        };
    }

    /// <summary>
    /// Scrubs, trims to the column width and blanks out, so that every free-text column is handled the
    /// same way and none of them can carry a credential or overflow.
    /// </summary>
    /// <remarks>
    /// Truncating rather than refusing is deliberate, and it matters most on the summary of a refused
    /// sign-in: the account name in it was typed by somebody who has not signed in, so a name longer than the
    /// column would otherwise throw on the way to recording the refusal — and the operator would see a
    /// different message from the one every other refusal shows.
    /// </remarks>
    private static string? Clean(string? value, int maxLength, string fieldName)
        => Guard.Optional(SensitiveText.Truncate(SensitiveText.Scrub(value), maxLength), maxLength, fieldName);
}
