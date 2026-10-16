using MediStock.Application.Common;
using MediStock.Application.Identity;
using MediStock.Domain.Common;
using MediStock.Domain.Identity;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Identity;

/// <summary>
/// Signing in, signing out, the first-run set-up and changing your own password.
/// </summary>
/// <remarks>
/// <para>
/// This is the only type that writes to <see cref="UserSession"/>. Everything else reads the session through
/// <see cref="IUserSession"/>, so there is one place to look to find out who is signed in and no way for a
/// screen to sign somebody in by accident.
/// </para>
/// <para>
/// Every attempt is audited, successful or not. A refused attempt is recorded as
/// <see cref="AuditEntry.SystemActor"/> under the name that was typed, which is what makes a run of refusals
/// visible on the trail — and is also why the row records the name in the summary rather than as an
/// identifier: there may be no such account.
/// </para>
/// <para>
/// The wording of a refusal never distinguishes an unknown account from a wrong password from a deactivated
/// account. All three answer in <see cref="IAuthenticationService.BadCredentialsMessage"/>, and all three
/// take the same time: the unknown-account path checks the password against a fixed decoy hash so that the
/// prompt cannot be used to find out which names exist. The audit row does distinguish them, because the
/// trail is for the administrator and not for the person typing.
/// </para>
/// </remarks>
internal sealed class EfAuthenticationService : IAuthenticationService
{
    /// <summary>
    /// A hash that no password matches, checked when there is no account to check against. Built with the
    /// same hasher, and therefore the same work factor, as real accounts: its cost is the point — refusing
    /// an unknown name must take as long as refusing a wrong password, or the prompt would reveal which
    /// names exist by timing the refusals.
    /// </summary>
    private readonly string _decoyHash;

    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly UserSession _session;
    private readonly IPasswordHasher _hasher;
    private readonly IClock _clock;
    private readonly ILogger<EfAuthenticationService> _logger;

    public EfAuthenticationService(
        IDbContextFactory<MediStockDbContext> factory,
        UserSession session,
        IPasswordHasher hasher,
        IClock clock,
        ILogger<EfAuthenticationService> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Through the injected hasher rather than a fixed hash, so the decoy can never cost less than a
        // real account's work factor and the unknown-account path stays indistinguishable in time.
        _decoyHash = _hasher.Hash("a password nobody types").Encoded;
    }

    public async Task<bool> IsSetUpAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Deliberately not through IUserDirectory: on a fresh installation nobody is signed in, so there is
        // nobody who could hold the permission to ask.
        return await db.Users.AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<UserDto>> SetUpFirstAdministratorAsync(
        UserDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.Validate() is { Count: > 0 } problems)
        {
            return OperationResult<UserDto>.Fail(problems);
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        if (await db.Users.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return OperationResult<UserDto>.Fail(
                "This installation already has accounts, so it cannot be set up again. Add an account from "
                + "the users screen instead.");
        }

        var now = _clock.UtcNow;

        var administrator = User.Create(
            draft.UserName,
            draft.DisplayName,
            Role.Administrator,
            _hasher.Hash(draft.Password!),
            now);

        administrator.RecordSignIn(now);

        db.Users.Add(administrator);

        // Recorded as the administrator themselves: they are the first account, and the row has to name
        // somebody. The set-up itself is the one administrative act that happens before there is a session.
        AuditJournal.Stage(
            db,
            new AuditRecord(
                AuditAction.UserCreated,
                $"This installation was set up with \"{administrator.UserName}\" as its first administrator.")
            {
                EntityType = nameof(User),
                EntityId = administrator.UserName,
                Reason = draft.Reason,
            },
            actor: administrator.UserName,
            userId: null);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("The installation was set up with {UserName} as its first administrator.", administrator.UserName);

        var dto = ToDto(administrator);
        _session.Adopt(dto);

        return OperationResult<UserDto>.Success(dto);
    }

    public async Task<SignInResult> SignInAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var typed = (userName ?? string.Empty).Trim();
        var normalised = Guard.NormaliseForComparison(typed);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        User? user = null;

        if (normalised is not null)
        {
            user = await db.Users
                .FirstOrDefaultAsync(u => u.NormalisedUserName == normalised, cancellationToken)
                .ConfigureAwait(false);
        }

        var matches = user is null
            ? CheckDecoy(password)
            : _hasher.Verify(password, user.PasswordHashEncoded);

        if (user is null || !matches)
        {
            await AuditRefusalAsync(
                db,
                typed,
                user is null
                    ? $"A sign-in was refused for the account name \"{typed}\", which does not exist here."
                    : $"A sign-in was refused for \"{user.UserName}\": the password did not match.",
                cancellationToken).ConfigureAwait(false);

            // One message for every kind of refusal, so that the prompt cannot be used to find out which
            // account names exist.
            return SignInResult.Refused(
                SignInFailure.BadCredentials,
                IAuthenticationService.BadCredentialsMessage);
        }

        if (!user.IsActive)
        {
            await AuditRefusalAsync(
                db,
                user.UserName,
                $"A sign-in was refused for \"{user.UserName}\": the account has been deactivated.",
                cancellationToken).ConfigureAwait(false);

            // Same words as a wrong password, deliberately. The operator who owns the account is told
            // nothing either, because telling them would tell a stranger the name exists.
            return SignInResult.Refused(
                SignInFailure.AccountDeactivated,
                IAuthenticationService.BadCredentialsMessage);
        }

        var now = _clock.UtcNow;

        user.RecordSignIn(now);

        // A hash written under an older, cheaper work factor is replaced while the password is in hand. It is
        // the only moment the plaintext is available, so it is the only moment this is possible.
        if (user.Password.NeedsRehash)
        {
            user.SetPassword(_hasher.Hash(password), now);
            _logger.LogInformation("The stored password for {UserName} was re-hashed at the current work factor.", user.UserName);
        }

        AuditJournal.Stage(
            db,
            new AuditRecord(
                AuditAction.SignInSucceeded,
                $"{user.FriendlyName} signed in.")
            {
                EntityType = nameof(User),
                EntityId = user.UserName,
            },
            actor: user.UserName,
            userId: user.Id);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var dto = ToDto(user);
        _session.Adopt(dto);

        return SignInResult.Success(dto);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var current = _session.CurrentUser;

        if (current is null)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        AuditJournal.Stage(
            db,
            new AuditRecord(AuditAction.SignOut, $"{current.FriendlyName} signed out.")
            {
                EntityType = nameof(User),
                EntityId = current.UserName,
            },
            actor: current.UserName,
            userId: current.Id);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _session.Clear();
    }

    public async Task<OperationResult> ChangeOwnPasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var current = _session.CurrentUser;

        if (current is null)
        {
            return OperationResult.Fail("Nobody is signed in, so there is no password to change.");
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == current.Id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return OperationResult.Fail("Your account no longer exists. Sign in again.");
        }

        if (!_hasher.Verify(currentPassword, user.PasswordHashEncoded))
        {
            return OperationResult.Fail("That is not your current password, so nothing was changed.");
        }

        if (PasswordPolicy.Validate(newPassword, user.UserName) is { Count: > 0 } problems)
        {
            return OperationResult.Fail(problems);
        }

        if (_hasher.Verify(newPassword, user.PasswordHashEncoded))
        {
            return OperationResult.Fail("The new password is the same as the old one. Choose a different one.");
        }

        user.SetPassword(_hasher.Hash(newPassword), _clock.UtcNow);

        AuditJournal.Stage(
            db,
            new AuditRecord(AuditAction.PasswordChanged, $"{user.FriendlyName} changed their own password.")
            {
                EntityType = nameof(User),
                EntityId = user.UserName,
            },
            actor: current.UserName,
            userId: current.Id);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The session survives: changing your own password should not throw you out of the screen you were
        // working on. The role and the identifier cannot have changed, so only the display fields are refreshed.
        _session.Refresh(ToDto(user));

        return OperationResult.Success();
    }

    /// <summary>
    /// Records a refused sign-in under the system account. The row names the account that was typed, for the
    /// administrator to read, and is written even when no such account exists.
    /// </summary>
    private async Task AuditRefusalAsync(
        MediStockDbContext db,
        string typed,
        string summary,
        CancellationToken cancellationToken)
    {
        AuditJournal.Stage(
            db,
            new AuditRecord(AuditAction.SignInFailed, summary) { EntityType = nameof(User) },
            actor: AuditEntry.SystemActor,
            userId: null);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The typed name is a free-text field in a log line and is scrubbed the same way the audit row is, so a
        // password typed into the account-name box does not end up in the log file.
        _logger.LogWarning(
            "A sign-in was refused for {AccountName}.",
            SensitiveText.Scrub(typed));
    }

    /// <summary>
    /// Checks a password against a hash that matches nothing, purely so that the unknown-account path costs
    /// the same as the wrong-password path.
    /// </summary>
    private bool CheckDecoy(string? password) => _hasher.Verify(password, _decoyHash);

    private static UserDto ToDto(User user) => new(
        user.Id,
        user.UserName,
        user.DisplayName,
        user.Role,
        user.IsActive,
        user.CreatedAtUtc,
        user.LastSignedInAtUtc);
}
