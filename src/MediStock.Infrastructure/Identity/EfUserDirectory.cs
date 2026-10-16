using System.Linq.Expressions;
using MediStock.Application.Common;
using MediStock.Application.Identity;
using MediStock.Domain.Common;
using MediStock.Domain.Identity;
using MediStock.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Identity;

/// <summary>
/// The accounts on this installation, over Entity Framework Core.
/// </summary>
/// <remarks>
/// <para>
/// Every method here requires <see cref="Permission.ManageUsers"/>, reads included: who works in the
/// pharmacy is itself administrative information. The one exception is the check for whether the
/// installation has been set up at all, which <see cref="EfAuthenticationService"/> makes for itself before
/// there can be anybody signed in to have a permission.
/// </para>
/// <para>
/// The last-administrator rule is enforced twice. The aggregate refuses to demote or deactivate the last
/// active administrator; this type, before it asks, counts the <em>other</em> active administrators inside
/// the transaction and with an update lock on the rows it counted, so that two administrators demoting each
/// other at the same moment cannot both see one other and both succeed. The lock is what makes the count
/// mean what the rule says it means.
/// </para>
/// </remarks>
internal sealed class EfUserDirectory : IUserDirectory
{
    private static readonly Expression<Func<User, UserDto>> Projection = u => new UserDto(
        u.Id,
        u.UserName,
        u.DisplayName,
        u.Role,
        u.IsActive,
        u.CreatedAtUtc,
        u.LastSignedInAtUtc);

    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly IUserSession _session;
    private readonly IPasswordHasher _hasher;
    private readonly IClock _clock;
    private readonly ILogger<EfUserDirectory> _logger;

    public EfUserDirectory(
        IDbContextFactory<MediStockDbContext> factory,
        IUserSession session,
        IPasswordHasher hasher,
        IClock clock,
        ILogger<EfUserDirectory> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Adds an audit row naming the administrator who made the change. Every method here changes somebody
    /// else's account, so the actor is always the signed-in person — never <c>system</c>, which would make
    /// the trail say only that an account changed without saying who changed it.
    /// </summary>
    private AuditEntry Stage(MediStockDbContext db, AuditRecord record)
    {
        var (actor, userId) = AuditJournal.ActorOf(_session);

        return AuditJournal.Stage(db, record, actor, userId);
    }

    public async Task<IReadOnlyList<UserDto>> SearchAsync(
        UserQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        _session.Require(Permission.ManageUsers, "see who has an account here");

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var source = db.Users.AsNoTracking();

        if (!query.IncludeInactive)
        {
            source = source.Where(u => u.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Term))
        {
            var pattern = $"%{EscapeLike(query.Term.Trim())}%";
            source = source.Where(u =>
                EF.Functions.Like(u.UserName, pattern) || EF.Functions.Like(u.DisplayName, pattern));
        }

        var take = Math.Clamp(query.Take, 1, UserQuery.MaxResults);

        return await source
            .OrderBy(u => u.DisplayName)
            .ThenBy(u => u.UserName)
            .ThenBy(u => u.Id)
            .Take(take)
            .Select(Projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<UserDto?> FindAsync(int userId, CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.ManageUsers, "open this account");

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(Projection)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.ManageUsers, "count the accounts");

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await db.Users.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<UserDto>> CreateAsync(
        UserDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        _session.Require(Permission.ManageUsers, "add an account");

        if (draft.Validate() is { Count: > 0 } problems)
        {
            return OperationResult<UserDto>.Fail(problems);
        }

        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var user = User.Create(
                draft.UserName,
                draft.DisplayName,
                draft.Role,
                _hasher.Hash(draft.Password!),
                _clock.UtcNow);

            db.Users.Add(user);

            Stage(
                db,
                new AuditRecord(
                    AuditAction.UserCreated,
                    $"Account \"{user.UserName}\" created with the {user.Role.Label()} role.")
                {
                    EntityType = nameof(User),
                    EntityId = user.UserName,
                    Reason = draft.Reason,
                });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Account {UserName} created by {Actor}.", user.UserName, _session.CurrentUser?.UserName);

            return OperationResult<UserDto>.Success(Project(user));
        }
        catch (DbUpdateException ex)
            when (SqlFailure.TryClassify(ex, out var kind, out _) && kind == SqlFailureKind.Duplicate)
        {
            // The unique index is the authority, not a lookup beforehand: two administrators adding the same
            // account name at the same moment must not both succeed.
            return OperationResult<UserDto>.Fail(
                $"There is already an account named \"{draft.UserName.Trim()}\". Account names are not "
                + "case-sensitive, so \"Imran\" and \"imran\" are the same name.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Creating the account {UserName} failed.", draft.UserName);

            return OperationResult<UserDto>.Fail(
                "The account could not be created. Check the application log for details.");
        }
    }

    public Task<OperationResult<UserDto>> SetRoleAsync(
        int userId,
        Role role,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.ManageUsers, "change somebody's role");

        if (!Enum.IsDefined(role))
        {
            return Task.FromResult(OperationResult<UserDto>.Fail($"'{role}' is not a known role."));
        }

        if (IsSelf(userId))
        {
            return Task.FromResult(OperationResult<UserDto>.Fail(
                "You cannot change your own role. Ask another administrator to do it: the permissions in force are "
                + "fixed when you sign in, so a role you changed yourself would take effect only at your next sign-in."));
        }

        return SetRoleCoreAsync(userId, role, reason, cancellationToken);
    }

    private async Task<OperationResult<UserDto>> SetRoleCoreAsync(
        int userId,
        Role role,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await UnitOfWorkRetry.ExecuteAsync(
            db,
            async () =>
            {
                // A repeated attempt starts from a cleared change tracker: the work a first attempt half-built
                // must not be carried into the second. See ADR 0006.
                db.ChangeTracker.Clear();

                var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
                if (user is null)
                {
                    return OperationResult<UserDto>.Fail("That account no longer exists.");
                }

                var previous = user.Role;
                if (previous == role)
                {
                    return OperationResult<UserDto>.Success(Project(user));
                }

                var others = await CountOtherActiveAdministratorsAsync(db, userId, cancellationToken).ConfigureAwait(false);

                user.ChangeRole(role, others, _clock.UtcNow);

                Stage(
                    db,
                    new AuditRecord(
                        AuditAction.UserRoleChanged,
                        $"Account \"{user.UserName}\" moved from {previous.Label()} to {role.Label()}.")
                    {
                        EntityType = nameof(User),
                        EntityId = user.UserName,
                        Reason = reason,
                        Detail = $"Was {previous}, now {role}.",
                    });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                return OperationResult<UserDto>.Success(Project(user));
            },
            "Somebody else changed this account at the same time. Open it again and repeat the change.",
            _logger,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<OperationResult<UserDto>> SetActiveAsync(
        int userId,
        bool isActive,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        _session.Require(
            Permission.ManageUsers,
            isActive ? "let somebody sign in again" : "stop somebody signing in");

        if (!isActive && IsSelf(userId))
        {
            return Task.FromResult(OperationResult<UserDto>.Fail(
                "You cannot deactivate the account you are signed in with. Ask another administrator to do it."));
        }

        return SetActiveCoreAsync(userId, isActive, reason, cancellationToken);
    }

    private async Task<OperationResult<UserDto>> SetActiveCoreAsync(
        int userId,
        bool isActive,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await UnitOfWorkRetry.ExecuteAsync(
            db,
            async () =>
            {
                db.ChangeTracker.Clear();

                var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
                if (user is null)
                {
                    return OperationResult<UserDto>.Fail("That account no longer exists.");
                }

                if (user.IsActive == isActive)
                {
                    return OperationResult<UserDto>.Success(Project(user));
                }

                var others = isActive
                    ? 0
                    : await CountOtherActiveAdministratorsAsync(db, userId, cancellationToken).ConfigureAwait(false);

                if (isActive)
                {
                    user.Reactivate(_clock.UtcNow);
                }
                else
                {
                    user.Deactivate(others, _clock.UtcNow);
                }

                Stage(
                    db,
                    new AuditRecord(
                        isActive
                            ? AuditAction.UserReactivated
                            : AuditAction.UserDeactivated,
                        isActive
                            ? $"Account \"{user.UserName}\" may sign in again."
                            : $"Account \"{user.UserName}\" may no longer sign in.")
                    {
                        EntityType = nameof(User),
                        EntityId = user.UserName,
                        Reason = reason,
                    });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                return OperationResult<UserDto>.Success(Project(user));
            },
            "Somebody else changed this account at the same time. Open it again and repeat the change.",
            _logger,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> ResetPasswordAsync(
        int userId,
        string newPassword,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.ManageUsers, "set somebody else's password");

        if (IsSelf(userId))
        {
            return OperationResult.Fail(
                "An administrator cannot set their own password from here. Use \"Change your password\", which "
                + "asks for the current one first.");
        }

        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return OperationResult.Fail("That account no longer exists.");
            }

            if (PasswordPolicy.Validate(newPassword, user.UserName) is { Count: > 0 } problems)
            {
                return OperationResult.Fail(problems);
            }

            user.SetPassword(_hasher.Hash(newPassword), _clock.UtcNow);

            // The new password itself is never written to the trail, not even its shape: only the fact that it
            // was somebody else who set it.
            Stage(
                db,
                new AuditRecord(
                    AuditAction.PasswordReset,
                    $"Password for \"{user.UserName}\" was set by an administrator.")
                {
                    EntityType = nameof(User),
                    EntityId = user.UserName,
                    Reason = reason,
                });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Password for {UserName} was reset by {Actor}.",
                user.UserName,
                _session.CurrentUser?.UserName);

            return OperationResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Resetting the password for user {UserId} failed.", userId);

            return OperationResult.Fail(
                "The password could not be changed. Check the application log for details.");
        }
    }

    /// <summary>
    /// The stored account as the screen sees it. The password is not part of <see cref="UserDto"/> and is not
    /// read here.
    /// </summary>
    private static UserDto Project(User user) => new(
        user.Id,
        user.UserName,
        user.DisplayName,
        user.Role,
        user.IsActive,
        user.CreatedAtUtc,
        user.LastSignedInAtUtc);

    /// <summary>
    /// Counts the other active administrators, holding an update lock on what it counted until the
    /// transaction ends.
    /// </summary>
    /// <remarks>
    /// Written as SQL rather than as a LINQ count because the count has to take locks to mean anything. Two
    /// transactions that each count one <em>other</em> administrator under
    /// <c>READ COMMITTED</c> would both be told the installation has a spare administrator and would both
    /// demote theirs, leaving none. <c>UPDLOCK</c> with <c>HOLDLOCK</c> makes the second one wait, so it reads
    /// the world as it will be after the first commits — and finds no spare, which the aggregate then refuses.
    /// The predicate is parameterised, and the identifier is an <see cref="int"/>.
    /// </remarks>
    private static Task<int> CountOtherActiveAdministratorsAsync(
        MediStockDbContext db,
        int excludeUserId,
        CancellationToken cancellationToken)
        => db.Database
            .SqlQuery<int>(
                $"""
                 SELECT COUNT(*) AS [Value]
                 FROM [dbo].[Users] WITH (UPDLOCK, HOLDLOCK)
                 WHERE [Role] = {(int)Role.Administrator}
                   AND [IsActive] = CAST(1 AS bit)
                   AND [Id] <> {excludeUserId}
                 """)
            .SingleAsync(cancellationToken);

    /// <summary>Escapes the wildcards a term may contain, so that searching for "_" finds "_".</summary>
    private static string EscapeLike(string term)
        => term.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    /// <summary>True when the operation is aimed at the account that is signed in.</summary>
    /// <remarks>
    /// Three operations refuse their own account, for two different reasons. The permissions in force are read
    /// once at sign-in, so an administrator who changed their own role would keep the old permissions until
    /// they signed out — the change would be recorded as applied and would not be. And setting your own
    /// password from this screen would sidestep <see cref="IAuthenticationService.ChangeOwnPasswordAsync"/>,
    /// whose whole point is that it demands the current password first. See ADR 0009.
    /// </remarks>
    private bool IsSelf(int userId) => _session.CurrentUser?.Id == userId;
}
