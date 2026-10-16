using MediStock.Domain.Identity;

namespace MediStock.Application.Identity;

/// <summary>
/// Where sensitive actions are written down, and where they are read back for a support call.
/// </summary>
/// <remarks>
/// <para>
/// Writing is unconditional: any part of the application may record what it did, because refusing to
/// record would be worse than recording too much. Reading requires
/// <see cref="Permission.ViewAuditTrail"/>.
/// </para>
/// <para>
/// An action that happens inside a database transaction — a checkout, a receipt, a return — must have its
/// audit row written in that same transaction, or the trail would claim something happened that was rolled
/// back, or miss something that committed. Those writers stage the row through the infrastructure's audit
/// journal rather than through this interface, and the reason is recorded in
/// <c>docs/decisions/0009-users-roles-and-the-audit-trail.md</c>.
/// </para>
/// </remarks>
public interface IAuditTrail
{
    /// <summary>
    /// Records something that is not part of a larger transaction — a sign-in, a user change, a backup, a
    /// price edit — and returns the row as it was stored.
    /// </summary>
    Task<AuditEntry> RecordAsync(AuditRecord record, CancellationToken cancellationToken = default);

    /// <summary>The trail matching <paramref name="query"/>, newest first unless told otherwise.</summary>
    Task<IReadOnlyList<AuditEntryDto>> SearchAsync(AuditQuery query, CancellationToken cancellationToken = default);

    /// <summary>How many rows match, ignoring <see cref="AuditQuery.Take"/>, so a screen can say "showing 500 of 12,340".</summary>
    Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken = default);

    /// <summary>The newest rows, for the diagnostics panel. Does not require the read permission.</summary>
    Task<IReadOnlyList<AuditEntryDto>> RecentAsync(int take, CancellationToken cancellationToken = default);
}
