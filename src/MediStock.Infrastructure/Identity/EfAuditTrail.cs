using System.Linq.Expressions;
using MediStock.Application.Identity;
using MediStock.Domain.Identity;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Identity;

/// <summary>
/// The audit trail over Entity Framework Core.
/// </summary>
/// <remarks>
/// <para>
/// Writing is open to any part of the application. Reading requires
/// <see cref="Permission.ViewAuditTrail"/>, and the check is made here rather than in a decorator because
/// this type is also the one <see cref="RecentAsync"/> uses, and the newest few rows are shown on the
/// diagnostics panel to any signed-in operator — a support call needs them, and they are not a secret: they
/// are the last few things that happened, with no credentials in them.
/// </para>
/// <para>
/// There is no method that updates or deletes a row, and no way to ask for one, which is what makes the
/// trail worth reading.
/// </para>
/// </remarks>
internal sealed class EfAuditTrail : IAuditTrail
{
    /// <summary>Single projection, so the screen and the diagnostics panel see the same shape.</summary>
    private static readonly Expression<Func<AuditEntry, AuditEntryDto>> Projection = e => new AuditEntryDto(
        e.Id,
        e.OccurredAtUtc,
        e.Action,
        e.Actor,
        e.UserId,
        e.Summary,
        e.EntityType,
        e.EntityId,
        e.Reason,
        e.Detail);

    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly IUserSession _session;
    private readonly ILogger<EfAuditTrail> _logger;

    public EfAuditTrail(
        IDbContextFactory<MediStockDbContext> factory,
        IUserSession session,
        ILogger<EfAuditTrail> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AuditEntry> RecordAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var (actor, userId) = AuditJournal.ActorOf(_session);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await AuditJournal
            .WriteAsync(db, record, actor, userId, _logger, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntryDto>> SearchAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        _session.Require(Permission.ViewAuditTrail, "read the audit trail");

        var bounded = query.Normalise();

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await Order(Filter(db.AuditLogs.AsNoTracking(), bounded), bounded)
            .Take(bounded.Take)
            .Select(Projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        _session.Require(Permission.ViewAuditTrail, "read the audit trail");

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await Filter(db.AuditLogs.AsNoTracking(), query.Normalise())
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntryDto>> RecentAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await db.AuditLogs
            .AsNoTracking()
            .OrderByDescending(e => e.OccurredAtUtc)
            .ThenByDescending(e => e.Id)
            .Take(Math.Clamp(take <= 0 ? 20 : take, 1, 200))
            .Select(Projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the filters that both the list and the count share, so that the count can never disagree with
    /// the list about what is being asked for.
    /// </summary>
    private static IQueryable<AuditEntry> Filter(IQueryable<AuditEntry> source, AuditQuery query)
    {
        if (query.From is { } from)
        {
            source = source.Where(e => e.OccurredAtUtc >= from);
        }

        if (query.To is { } to)
        {
            source = source.Where(e => e.OccurredAtUtc <= to);
        }

        if (query.Action is { } action)
        {
            source = source.Where(e => e.Action == action);
        }

        if (query.Actor is { } actor)
        {
            source = source.Where(e => e.Actor == actor);
        }

        if (query.EntityType is { } entityType)
        {
            source = source.Where(e => e.EntityType == entityType);
        }

        if (query.Term is { } term)
        {
            var pattern = $"%{EscapeLike(term)}%";
            source = source.Where(e =>
                EF.Functions.Like(e.Summary, pattern)
                || EF.Functions.Like(e.Actor, pattern)
                || (e.Reason != null && EF.Functions.Like(e.Reason, pattern))
                || (e.Detail != null && EF.Functions.Like(e.Detail, pattern))
                || (e.EntityId != null && EF.Functions.Like(e.EntityId, pattern)));
        }

        return source;
    }

    /// <summary>
    /// Newest first by default, because that is what a support call asks for. The identifier breaks ties
    /// within the same millisecond, so two rows written in one transaction still have a stable order.
    /// </summary>
    private static IQueryable<AuditEntry> Order(IQueryable<AuditEntry> source, AuditQuery query)
        => query.NewestFirst
            ? source.OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Id)
            : source.OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Id);

    /// <summary>Escapes the wildcards a term may contain, so that searching for "50%" finds "50%".</summary>
    private static string EscapeLike(string term)
        => term.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
}
