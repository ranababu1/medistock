using MediStock.Application.Identity;
using MediStock.Domain.Identity;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Identity;

/// <summary>
/// The one place audit rows are added to a context, so that every writer records the trail the same way and
/// no writer has to remember how.
/// </summary>
/// <remarks>
/// <para>
/// There are two ways in, and the difference matters. <see cref="Stage"/> adds a row to a context that is
/// already in the middle of somebody else's transaction — a checkout, a receipt, a return — so the audit row
/// commits or rolls back with the thing it describes. Nothing is saved here: the caller's existing
/// <c>SaveChangesAsync</c> does that, which keeps the audit row inside the same transaction and not merely
/// inside the same request.
/// </para>
/// <para>
/// The second way is the application-facing <c>IAuditTrail</c>, which writes immediately in its own context.
/// It is for things that are not part of a larger transaction: a sign-in, a user change, a backup. That split
/// is deliberate and is argued in <c>docs/decisions/0009-users-roles-and-the-audit-trail.md</c>.
/// </para>
/// <para>
/// Audit writes are never wrapped in a retry of their own. A row added to a transaction that is being re-run
/// is discarded with the rest of that attempt's change tracker, so a retry cannot double-record.
/// </para>
/// </remarks>
internal static class AuditJournal
{
    /// <summary>
    /// Adds a row to <paramref name="db"/> without saving, so it commits with whatever the caller is in the
    /// middle of writing.
    /// </summary>
    /// <returns>The tracked row, whose identifier is filled in once the caller saves.</returns>
    internal static AuditEntry Stage(MediStockDbContext db, AuditRecord record)
        => Stage(db, record, actor: null, userId: null);

    /// <summary>
    /// Adds a row, naming the actor explicitly. Used where the session is not the person who acted: a
    /// refused sign-in has no session at all, and the seeder writes rows as <c>system</c>.
    /// </summary>
    internal static AuditEntry Stage(MediStockDbContext db, AuditRecord record, string? actor, int? userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(record);

        var entry = AuditEntry.Record(
            record.Action,
            record.Summary,
            record.Actor ?? actor,
            record.UserId ?? userId,
            record.EntityType,
            record.EntityId,
            record.Reason,
            record.Detail);

        db.AuditLogs.Add(entry);

        return entry;
    }

    /// <summary>
    /// Adds a row and saves it on its own, for the callers whose work is not already inside a transaction.
    /// </summary>
    internal static async Task<AuditEntry> WriteAsync(
        MediStockDbContext db,
        AuditRecord record,
        string? actor,
        int? userId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var entry = Stage(db, record, actor, userId);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The action, not the summary: a summary is free text and may quote what the operator typed.
        logger.LogInformation(
            "Audit {Action} by {Actor} recorded as row {AuditId}.",
            entry.Action,
            entry.Actor,
            entry.Id);

        return entry;
    }

    /// <summary>The name to record for whoever is acting, falling back to the system when nobody is signed in.</summary>
    internal static (string Actor, int? UserId) ActorOf(IUserSession? session)
    {
        var current = session?.CurrentUser;

        return current is null
            ? (AuditEntry.SystemActor, null)
            : (current.UserName, current.Id);
    }
}
