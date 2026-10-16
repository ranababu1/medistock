using MediStock.Application.Identity;
using MediStock.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Identity;

/// <summary>
/// Signing in, signing out and changing your own password, against a real database.
/// </summary>
[Collection(TestDatabaseCollection.Name)]
public class AuthenticationTests
{
    private const string Administrator = TestDatabaseFixture.AdministratorUserName;
    private const string AdministratorPassword = TestDatabaseFixture.AdministratorPassword;

    private readonly TestDatabaseFixture _fixture;

    public AuthenticationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task This_installation_is_set_up_because_the_fixture_made_an_administrator()
    {
        var setUp = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IAuthenticationService>().IsSetUpAsync());

        Assert.True(setUp);
    }

    [Fact]
    public async Task An_unknown_account_a_wrong_password_and_a_deactivated_account_are_refused_in_the_same_words()
    {
        // The whole point of the shared wording: a stranger at the sign-in prompt must not be able to use it to
        // find out which account names exist. What differs, if anything, is the audit row.
        var name = $"it.deafened.{Stamp()}";

        var results = await _fixture.InScopeAsync(async services =>
        {
            var authentication = services.GetRequiredService<IAuthenticationService>();

            var unknown = await authentication.SignInAsync(name, "Juniper-Trellis-58");
            var wrongPassword = await authentication.SignInAsync(Administrator, "Juniper-Trellis-58");
            var blank = await authentication.SignInAsync(string.Empty, string.Empty);

            return new[] { unknown, wrongPassword, blank };
        });

        foreach (var result in results)
        {
            Assert.False(result.Succeeded);
            Assert.Equal(IAuthenticationService.BadCredentialsMessage, result.Message);
            Assert.Null(result.User);
        }

        Assert.Equal(SignInFailure.BadCredentials, results[0].Failure);
        Assert.Equal(SignInFailure.BadCredentials, results[1].Failure);

        // The session the rest of the run uses is untouched by a refused attempt.
        var stillSignedIn = await _fixture.InScopeAsync(services =>
            Task.FromResult(services.GetRequiredService<IUserSession>().CurrentUser?.UserName));

        Assert.Equal(Administrator, stillSignedIn);
    }

    [Fact]
    public async Task A_deactivated_account_cannot_sign_in_even_with_the_right_password()
    {
        var name = NewName("stopped");
        const string password = "Marigold-Till-92";

        var (refusal, failure) = await _fixture.InScopeAsync(async services =>
        {
            var directory = services.GetRequiredService<IUserDirectory>();
            var created = await directory.CreateAsync(new UserDraft
            {
                UserName = name,
                DisplayName = "Stopped Person",
                Role = Role.Cashier,
                Password = password,
                ConfirmPassword = password,
            });

            Assert.True(created.Succeeded, created.Message);

            var stopped = await directory.SetActiveAsync(created.Value!.Id, false, "Left the pharmacy.");
            Assert.True(stopped.Succeeded, stopped.Message);

            var result = await services.GetRequiredService<IAuthenticationService>().SignInAsync(name, password);

            return (result, result.Failure);
        });

        Assert.False(refusal.Succeeded);

        // The caller is told which kind of refusal it was, and the operator is told nothing.
        Assert.Equal(SignInFailure.AccountDeactivated, failure);
        Assert.Equal(IAuthenticationService.BadCredentialsMessage, refusal.Message);
    }

    [Fact]
    public async Task The_trail_distinguishes_what_the_prompt_deliberately_does_not()
    {
        var unknown = $"it.ghost.{Stamp()}";
        var known = NewName("mismatch");

        var rows = await _fixture.InScopeAsync(async services =>
        {
            var directory = services.GetRequiredService<IUserDirectory>();
            var password = "Marigold-Till-92";

            var created = await directory.CreateAsync(new UserDraft
            {
                UserName = known,
                DisplayName = "Mismatched Person",
                Role = Role.Cashier,
                Password = password,
                ConfirmPassword = password,
            });

            Assert.True(created.Succeeded, created.Message);

            var authentication = services.GetRequiredService<IAuthenticationService>();
            await authentication.SignInAsync(unknown, "Juniper-Trellis-58");
            await authentication.SignInAsync(known, "Juniper-Trellis-58");

            var trail = services.GetRequiredService<IAuditTrail>();

            return (
                Unknown: (await trail.SearchAsync(new AuditQuery { Action = AuditAction.SignInFailed, Term = unknown, Take = 5 })).Single(),
                Wrong: (await trail.SearchAsync(new AuditQuery { Action = AuditAction.SignInFailed, Term = known, Take = 5 })).Single());
        });

        // Nobody was signed in when these were written, so they name the system account rather than a person.
        Assert.Equal(AuditEntry.SystemActor, rows.Unknown.Actor);
        Assert.Equal(AuditEntry.SystemActor, rows.Wrong.Actor);
        Assert.Null(rows.Unknown.UserId);

        // The account name is in the summary rather than in the identifier, because there may be no account.
        Assert.Null(rows.Unknown.EntityId);
        Assert.Contains("does not exist here", rows.Unknown.Summary);
        Assert.Contains("the password did not match", rows.Wrong.Summary);
    }

    [Fact]
    public async Task An_account_name_longer_than_the_summary_column_is_refused_rather_than_crashing()
    {
        // The name in the summary was typed by somebody who has not signed in, so nothing has bounded it. It has
        // to be cut to fit the column, or the refusal would throw and the operator would see a different message
        // from the one every other refusal shows.
        var typed = new string('z', 600);

        var (result, row) = await _fixture.InScopeAsync(async services =>
        {
            var refusal = await services.GetRequiredService<IAuthenticationService>().SignInAsync(typed, "Juniper-Trellis-58");

            var trail = await services.GetRequiredService<IAuditTrail>().SearchAsync(new AuditQuery
            {
                Action = AuditAction.SignInFailed,
                Take = 5,
            });

            return (refusal, trail.First(r => r.Summary.Contains("zzz")));
        });

        Assert.Equal(IAuthenticationService.BadCredentialsMessage, result.Message);
        Assert.Equal(AuditEntry.SummaryMaxLength, row.Summary.Length);
        Assert.EndsWith("\u2026", row.Summary);
    }

    [Fact]
    public async Task A_successful_sign_in_records_when_it_happened_and_who_it_was()
    {
        var name = NewName("welcome");
        const string password = "Marigold-Till-92";

        var (before, after, refusal) = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                var directory = services.GetRequiredService<IUserDirectory>();
                var created = await directory.CreateAsync(new UserDraft
                {
                    UserName = name,
                    DisplayName = "Welcome Person",
                    Role = Role.Cashier,
                    Password = password,
                    ConfirmPassword = password,
                });

                Assert.True(created.Succeeded, created.Message);
                Assert.Null(created.Value!.LastSignedInAtUtc);

                var signedIn = await services.GetRequiredService<IAuthenticationService>().SignInAsync(name, password);
                Assert.True(signedIn.Succeeded, signedIn.Message);
                Assert.Equal("Welcome Person", signedIn.User!.FriendlyName);
                Assert.Equal(Role.Cashier, signedIn.User.Role);
                Assert.True(signedIn.User.LastSignedInAtUtc > DateTimeOffset.UtcNow.AddMinutes(-5));

                // A cashier may not read the trail, so the new account cannot look up its own welcome.
                var refused = await Assert.ThrowsAsync<PermissionDeniedException>(() =>
                    services.GetRequiredService<IAuditTrail>().SearchAsync(new AuditQuery
                    {
                        Action = AuditAction.SignInSucceeded,
                        Term = name,
                        Take = 5,
                    }));

                return (created.Value.LastSignedInAtUtc, signedIn.User.LastSignedInAtUtc, refused.Message);
            }
            finally
            {
                await SignInAsAdministratorAsync(services);
            }
        });

        var row = await _fixture.InScopeAsync(async services =>
            (await services.GetRequiredService<IAuditTrail>().SearchAsync(new AuditQuery
            {
                Action = AuditAction.SignInSucceeded,
                Term = name,
                Take = 5,
            })).Single(entry => entry.EntityId == name));

        Assert.Null(before);
        Assert.NotNull(after);
        Assert.Contains($"permission: {Permission.ViewAuditTrail}", refusal);
        Assert.Equal(name, row.Actor);
        Assert.Equal("Welcome Person signed in.", row.Summary);
    }

    [Fact]
    public async Task Signing_out_records_the_end_of_the_session_and_forgets_who_it_was()
    {
        var rows = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                var authentication = services.GetRequiredService<IAuthenticationService>();
                await authentication.SignOutAsync();

                var session = services.GetRequiredService<IUserSession>();
                Assert.False(session.IsSignedIn);
                Assert.Null(session.CurrentUser);
                Assert.Equal(PermissionSet.Empty.Count, session.Permissions.Count);

                // Signing out twice is harmless: there is nobody left to name on the second row.
                await authentication.SignOutAsync();

                var trail = services.GetRequiredService<IAuditTrail>();

                return await trail.RecentAsync(10);
                
            }
            finally
            {
                await SignInAsAdministratorAsync(services);
            }
        });

        // Signing out twice is harmless: there is nobody left to name on the second row. Recent activity needs
        // no permission, so the newest rows are readable even though the trail itself is the administrator's.
        Assert.Equal(AuditAction.SignOut, rows[0].Action);

        // Older sign-outs by the same administrator are in the trail too, so the newest one is the row to read.
        var row = rows.First(entry => entry.Action == AuditAction.SignOut && entry.Actor == Administrator);
        Assert.Equal("Integration Administrator signed out.", row.Summary);
    }

    [Fact]
    public async Task Changing_your_own_password_needs_the_current_one_and_keeps_you_signed_in()
    {
        var name = NewName("selfservice");
        const string original = "Marigold-Till-92";
        const string replacement = "Juniper-Trellis-58";

        var (wrongCurrent, sameAsOld, weak, changed, stillSignedIn, refusal) = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                var directory = services.GetRequiredService<IUserDirectory>();
                var created = await directory.CreateAsync(new UserDraft
                {
                    UserName = name,
                    DisplayName = "Self Service",
                    Role = Role.Pharmacist,
                    Password = original,
                    ConfirmPassword = original,
                });

                Assert.True(created.Succeeded, created.Message);

                var authentication = services.GetRequiredService<IAuthenticationService>();
                var session = services.GetRequiredService<IUserSession>();

                Assert.True((await authentication.SignInAsync(name, original)).Succeeded);

                var refusedCurrent = await authentication.ChangeOwnPasswordAsync("Juniper-Trellis-58", replacement);
                var refusedSame = await authentication.ChangeOwnPasswordAsync(original, original);
                var refusedWeak = await authentication.ChangeOwnPasswordAsync(original, "indi");
                var accepted = await authentication.ChangeOwnPasswordAsync(original, replacement);

                var refusedRead = await Assert.ThrowsAsync<PermissionDeniedException>(() =>
                    services.GetRequiredService<IAuditTrail>().SearchAsync(new AuditQuery
                    {
                        Action = AuditAction.PasswordChanged,
                        Term = name,
                        Take = 5,
                    }));

                // The session survives: changing your own password must not throw you back to the prompt.
                return (
                    refusedCurrent,
                    refusedSame,
                    refusedWeak,
                    accepted,
                    session.CurrentUser?.UserName,
                    refusedRead.Message);
            }
            finally
            {
                await SignInAsAdministratorAsync(services);
            }
        });

        Assert.False(wrongCurrent.Succeeded);
        Assert.Equal("That is not your current password, so nothing was changed.", wrongCurrent.Message);
        Assert.False(sameAsOld.Succeeded);
        Assert.Equal("The new password is the same as the old one. Choose a different one.", sameAsOld.Message);
        Assert.False(weak.Succeeded);
        Assert.Contains($"at least {PasswordPolicy.MinimumLength} characters", weak.Message);
        Assert.True(changed.Succeeded, changed.Message);
        Assert.Equal(name, stillSignedIn);
        Assert.Contains($"permission: {Permission.ViewAuditTrail}", refusal);

        var row = await _fixture.InScopeAsync(async services =>
            (await services.GetRequiredService<IAuditTrail>().SearchAsync(new AuditQuery
            {
                Action = AuditAction.PasswordChanged,
                Term = name,
                Take = 5,
            })).Single(entry => entry.EntityId == name));

        Assert.Equal(name, row.Actor);
        Assert.Equal("Self Service changed their own password.", row.Summary);
        Assert.DoesNotContain(replacement, row.Summary);

        var newPasswordWorks = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                var signedIn = await services.GetRequiredService<IAuthenticationService>()
                    .SignInAsync(name, replacement);

                Assert.True(signedIn.Succeeded, signedIn.Message);

                var withOld = await services.GetRequiredService<IAuthenticationService>().SignInAsync(name, original);

                return withOld.Succeeded;
            }
            finally
            {
                await SignInAsAdministratorAsync(services);
            }
        });

        Assert.False(newPasswordWorks, "The old password must have stopped working.");
    }

    [Fact]
    public async Task Changing_a_password_with_nobody_signed_in_is_refused()
    {
        var failure = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                await services.GetRequiredService<IAuthenticationService>().SignOutAsync();

                return await services.GetRequiredService<IAuthenticationService>()
                    .ChangeOwnPasswordAsync("Marigold-Till-92", "Juniper-Trellis-58");
            }
            finally
            {
                await SignInAsAdministratorAsync(services);
            }
        });

        Assert.False(failure.Succeeded);
        Assert.Equal("Nobody is signed in, so there is no password to change.", failure.Message);
    }

    [Fact]
    public async Task An_installation_that_already_has_accounts_cannot_be_set_up_again()
    {
        // Otherwise the first-run bootstrap would be a way to add an administrator to a working installation
        // without holding the ManageUsers permission.
        var attempt = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IAuthenticationService>().SetUpFirstAdministratorAsync(new UserDraft
            {
                UserName = NewName("intruder"),
                DisplayName = "Intruder",
                Role = Role.Administrator,
                Password = "Juniper-Trellis-58",
                ConfirmPassword = "Juniper-Trellis-58",
            }));

        Assert.False(attempt.Succeeded);
        Assert.Contains("already has accounts", attempt.Message);
    }

    private static async Task SignInAsAdministratorAsync(IServiceProvider services)
    {
        var signedIn = await services.GetRequiredService<IAuthenticationService>()
            .SignInAsync(Administrator, AdministratorPassword);

        Assert.True(signedIn.Succeeded, $"Could not hand the session back to the administrator: {signedIn.Message}");
    }

    private static string NewName(string stem) => $"it.{stem}.{Stamp()}";

    private static string Stamp() => Guid.NewGuid().ToString("N")[..8];
}
