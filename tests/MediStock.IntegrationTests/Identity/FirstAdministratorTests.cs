using MediStock.Application.Identity;
using MediStock.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Identity;

/// <summary>
/// The first run of a new installation, told in the order it happens: an empty database admits nobody, it
/// can be set up exactly once, and afterwards no route through the application can leave it with no
/// administrator.
/// </summary>
/// <remarks>
/// <para>
/// This runs against a database of its own because setting an installation up can only happen once, and
/// because it ends holding the only account that may administer anything. Every other test in the suite
/// starts from a database that has already been through this, which is why the story is one test rather
/// than several sharing the fresh database.
/// </para>
/// <para>
/// The last-administrator rule is enforced twice: <see cref="User"/> refuses a change that would leave no
/// active administrator, and the directory counts the <em>other</em> active administrators under an update
/// lock before it asks. What the application can reach are the second guard and the two self-guards, so
/// those are what this test drives.
/// </para>
/// </remarks>
[Collection(TestDatabaseCollection.Name)]
public sealed class FirstAdministratorTests : IClassFixture<ScratchIdentityDatabase>
{
    private const string FirstUser = "opening.admin";
    private const string FirstDisplay = "Opening Administrator";
    private const string SecondUser = "second.admin";
    private const string SetupReason = "The pharmacy opens tomorrow.";

    // Assembled at run time so a reader and a scanner both see a value that is obviously made up for the
    // duration of one test rather than a credential that was checked in.
    private static readonly string FirstPassword = string.Concat("Fennel", "-Harbour-", "73");
    private static readonly string SecondPassword = string.Concat("Juniper", "-Orchard-", "58");
    private static readonly string TooShort = string.Concat("short", "-", "9");

    private readonly ScratchDatabase _database;

    public FirstAdministratorTests(ScratchIdentityDatabase scratch) => _database = scratch.Database;

    [Fact]
    public async Task A_new_installation_is_set_up_once_and_can_never_be_left_without_an_administrator()
    {
        // 1. An installation with no accounts asks to be set up, and admits nobody. The application shows the
        //    first-run screen instead of the sign-in dialog while IsSetUpAsync is false.
        var isSetUp = await _database.InScopeAsync(services =>
            services.GetRequiredService<IAuthenticationService>().IsSetUpAsync());

        Assert.False(isSetUp, $"{_database.Name} was expected to have no accounts yet.");

        var early = await _database.InScopeAsync(services =>
            services.GetRequiredService<IAuthenticationService>()
                .SignInAsync(FirstUser, FirstPassword, CancellationToken.None));

        Assert.False(early.Succeeded);
        Assert.Null(early.User);
        Assert.Equal(IAuthenticationService.BadCredentialsMessage, early.Message);

        // 2. The set-up itself is refused while the password would not do, and says why.
        var tooShort = await _database.InScopeAsync(services =>
            services.GetRequiredService<IAuthenticationService>()
                .SetUpFirstAdministratorAsync(NewFirstAdministrator(TooShort)));

        Assert.False(tooShort.Succeeded);
        Assert.Contains($"at least {PasswordPolicy.MinimumLength}", tooShort.Message, StringComparison.Ordinal);

        // 3. A good set-up creates the administrator, signs them in, and records both facts.
        var (created, signedIn, firstRow) = await _database.InScopeAsync(async services =>
        {
            var result = await services.GetRequiredService<IAuthenticationService>()
                .SetUpFirstAdministratorAsync(NewFirstAdministrator(FirstPassword));

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(Role.Administrator, result.Value?.Role);

            // PermissionSet holds no value equality, so an administrator is checked by what the set grants.
            Assert.True(result.Value!.Permissions.HasAll(Enum.GetValues<Permission>()));

            var session = services.GetRequiredService<IUserSession>().CurrentUser;
            var row = (await services.GetRequiredService<IAuditTrail>().RecentAsync(1, CancellationToken.None))[0];

            return (result, session, row);
        });

        Assert.Equal(FirstUser, created.Value?.UserName);
        Assert.Equal(FirstUser, signedIn?.UserName);

        // The bootstrap signs the new administrator in: the first thing they see is the shell, not a second
        // request for the password they just chose.
        Assert.Equal(AuditAction.UserCreated, firstRow.Action);
        Assert.Equal(FirstUser, firstRow.Actor);
        Assert.Equal(SetupReason, firstRow.Reason);
        Assert.Contains($"\"{FirstUser}\" as its first administrator", firstRow.Summary, StringComparison.Ordinal);

        // 4. Setting up again is refused rather than quietly adding a second first administrator.
        Assert.True(await _database.InScopeAsync(services =>
            services.GetRequiredService<IAuthenticationService>().IsSetUpAsync()));

        var again = await _database.InScopeAsync(services =>
            services.GetRequiredService<IAuthenticationService>()
                .SetUpFirstAdministratorAsync(NewFirstAdministrator(FirstPassword)));

        Assert.False(again.Succeeded);
        Assert.Contains("already has accounts", again.Message, StringComparison.Ordinal);

        // 5. No password reaches the trail, in the set-up row or in the refusal that came before it.
        var rows = await _database.InScopeAsync(services =>
            services.GetRequiredService<IAuditTrail>().RecentAsync(50, CancellationToken.None));

        var refusedSignIn = Assert.Single(rows, row => row.Action == AuditAction.SignInFailed);

        // A refused sign-in is one of the very few rows with no actor: until the name and password are known
        // to match an account there is nobody to name, and naming the attempted account would confirm it.
        Assert.Equal(AuditEntry.SystemActor, refusedSignIn.Actor);

        foreach (var row in rows)
        {
            Assert.DoesNotContain(FirstPassword, $"{row.Summary}{row.Reason}{row.Detail}", StringComparison.Ordinal);
        }

        // 6. Nobody may administer their own account, which is what keeps the last administrator in place:
        //    the only accounts that may be changed are changed by an administrator, and no administrator may
        //    be the target of their own change. User.ChangeRole and User.SetActive are unit-tested for the
        //    underlying rule; what is proven here is that the routes out of the application stop at the guard.
        var (selfRole, selfDeactivate, moved) = await _database.InScopeAsync(async services =>
        {
            var directory = services.GetRequiredService<IUserDirectory>();
            var self = services.GetRequiredService<IUserSession>().CurrentUser
                ?? throw new InvalidOperationException("The set-up should have signed the administrator in.");

            var ownRole = await directory.SetRoleAsync(self.Id, Role.Cashier, "Stepping down.", CancellationToken.None);
            var ownAccount = await directory.SetActiveAsync(self.Id, false, "Leaving.", CancellationToken.None);

            Assert.False(ownRole.Succeeded);
            Assert.False(ownAccount.Succeeded);

            // Somebody else can still be administered, so the guard is a guard rather than a wall.
            var second = await directory.CreateAsync(
                new UserDraft
                {
                    UserName = SecondUser,
                    DisplayName = "Second Administrator",
                    Role = Role.Administrator,
                    Password = SecondPassword,
                    ConfirmPassword = SecondPassword,
                    Reason = "A spare, so the pharmacy is not locked out if one of us is away.",
                },
                CancellationToken.None);

            Assert.True(second.Succeeded, second.Message);

            var changed = await directory.SetRoleAsync(
                second.Value!.Id,
                Role.Cashier,
                "Moving the second account out of administration.",
                CancellationToken.None);

            // The account name is the identity, not the way it was typed: an account differing only in case
            // from one that exists cannot be added and confused for it.
            var impostor = await directory.CreateAsync(
                new UserDraft
                {
                    UserName = FirstUser.ToUpperInvariant(),
                    DisplayName = "Impostor",
                    Role = Role.Administrator,
                    Password = FirstPassword,
                    ConfirmPassword = FirstPassword,
                    Reason = "Seeing whether the name check ignores case.",
                },
                CancellationToken.None);

            Assert.False(impostor.Succeeded);

            return (ownRole, ownAccount, changed);
        });

        Assert.Contains("cannot change your own role", selfRole.Message, StringComparison.Ordinal);
        Assert.Contains("cannot deactivate the account you are signed in with", selfDeactivate.Message, StringComparison.Ordinal);

        Assert.True(moved.Succeeded, moved.Message);
        Assert.Equal(Role.Cashier, moved.Value?.Role);

        // A cashier rings up sales and nothing else.
        Assert.True(moved.Value!.Permissions.Has(Permission.Sell));
        Assert.False(moved.Value!.Permissions.Has(Permission.ManageUsers));

        // The demotion is on the record, naming the administrator who made the change rather than its target.
        var roleRow = (await _database.InScopeAsync(services =>
            services.GetRequiredService<IAuditTrail>().RecentAsync(50, CancellationToken.None)))
            .First(row => row.Action == AuditAction.UserRoleChanged);

        Assert.Equal(FirstUser, roleRow.Actor);
        Assert.Equal(SecondUser, roleRow.EntityId);
        Assert.Equal("Moving the second account out of administration.", roleRow.Reason);
    }

    private static UserDraft NewFirstAdministrator(string password) => new()
    {
        UserName = FirstUser,
        DisplayName = FirstDisplay,
        Role = Role.Administrator,
        Password = password,
        ConfirmPassword = password,
        Reason = SetupReason,
    };
}
