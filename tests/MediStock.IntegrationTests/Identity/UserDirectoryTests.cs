using MediStock.Application.Identity;
using MediStock.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Identity;

/// <summary>
/// The accounts on this installation, against a real database.
/// </summary>
/// <remarks>
/// The fixture signs in as <c>integration.admin</c> and stays signed in for the whole run, because almost every
/// other test is about a business rule rather than about permissions. These tests therefore have to be careful
/// with that administrator: one deactivates an account, one demotes it, one signs somebody else in — and every
/// one of them hands the session back the way it was found. The last-administrator rule is exercised against a
/// scratch database in <see cref="LastAdministratorTests"/>, because proving it here would mean briefly leaving
/// this installation with nobody in charge of it.
/// </remarks>
[Collection(TestDatabaseCollection.Name)]
public class UserDirectoryTests
{
    private const string Administrator = TestDatabaseFixture.AdministratorUserName;

    private readonly TestDatabaseFixture _fixture;

    public UserDirectoryTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Search_finds_an_account_by_a_fragment_of_its_name_ignoring_case()
    {
        var name = NewName("frag");

        var found = await _fixture.InScopeAsync(async services =>
        {
            await CreateAsync(services, name, "Fragmented Tester");

            return await services.GetRequiredService<IUserDirectory>()
                .SearchAsync(new UserQuery { Term = name[4..].ToUpperInvariant() });
        });

        Assert.Contains(found, u => u.UserName == name);
    }

    [Fact]
    public async Task Search_matches_the_display_name_as_well_as_the_account_name()
    {
        var display = $"Displayed Person {Stamp()}";

        var found = await _fixture.InScopeAsync(async services =>
        {
            await CreateAsync(services, NewName("shown"), display);

            return await services.GetRequiredService<IUserDirectory>().SearchAsync(new UserQuery { Term = display[..24] });
        });

        Assert.Contains(found, u => u.DisplayName == display);
    }

    [Fact]
    public async Task Search_orders_by_display_name()
    {
        var suffix = Stamp();

        var found = await _fixture.InScopeAsync(async services =>
        {
            await CreateAsync(services, NewName("orderb"), $"Ordered B {suffix}");
            await CreateAsync(services, NewName("ordera"), $"Ordered A {suffix}");

            return await services.GetRequiredService<IUserDirectory>().SearchAsync(new UserQuery { Term = suffix });
        });

        Assert.Equal(
            found.Select(u => u.DisplayName).OrderBy(n => n, StringComparer.Ordinal),
            found.Select(u => u.DisplayName));
    }

    [Fact]
    public async Task Search_treats_a_wildcard_in_the_term_as_a_literal_character()
    {
        // Unescaped, "_" would stand for any single character and an administrator looking for one account
        // would be shown several.
        var stem = $"wild{Stamp()}";

        var found = await _fixture.InScopeAsync(async services =>
        {
            await CreateAsync(services, $"{stem}_one", $"{stem} Underscore");
            await CreateAsync(services, $"{stem}Xone", $"{stem} Letter");

            return await services.GetRequiredService<IUserDirectory>().SearchAsync(new UserQuery { Term = $"{stem}_one" });
        });

        Assert.Single(found);
        Assert.Equal($"{stem}_one", found[0].UserName);
    }

    [Fact]
    public async Task A_deactivated_account_leaves_the_list_but_keeps_its_history()
    {
        var name = NewName("hidden");

        var (listed, listedWithInactive) = await _fixture.InScopeAsync(async services =>
        {
            var created = await CreateAsync(services, name, "Hidden Person");
            var directory = services.GetRequiredService<IUserDirectory>();

            var stopped = await directory.SetActiveAsync(created.Id, false, "Left the pharmacy.");
            Assert.True(stopped.Succeeded, stopped.Message);

            return (
                await directory.SearchAsync(new UserQuery { Term = name }),
                await directory.SearchAsync(new UserQuery { Term = name, IncludeInactive = true }));
        });

        Assert.DoesNotContain(listed, u => u.UserName == name);
        Assert.Contains(listedWithInactive, u => u.UserName == name && u.IsDeactivated);

        // Deliberately left deactivated: nothing in the application deletes an account, and the row it is
        // named on is still there to be read.
    }

    [Fact]
    public async Task Find_answers_null_for_an_account_that_does_not_exist()
    {
        var found = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IUserDirectory>().FindAsync(987_654_321));

        Assert.Null(found);
    }

    [Fact]
    public async Task Count_includes_the_deactivated_accounts()
    {
        var name = NewName("counted");

        var (before, after) = await _fixture.InScopeAsync(async services =>
        {
            var directory = services.GetRequiredService<IUserDirectory>();
            var start = await directory.CountAsync();

            var created = await CreateAsync(services, name, "Counted Person");
            await directory.SetActiveAsync(created.Id, false, "Ages out of the list.");

            return (start, await directory.CountAsync());
        });

        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task Create_refuses_a_name_that_is_already_taken_ignoring_case()
    {
        var name = NewName("Dup");

        var failure = await _fixture.InScopeAsync(async services =>
        {
            await CreateAsync(services, name, "First Owner");

            return await services.GetRequiredService<IUserDirectory>()
                .CreateAsync(Draft(name.ToUpperInvariant(), "Second Owner"));
        });

        Assert.False(failure.Succeeded);
        Assert.Contains("not case-sensitive", failure.Message);
        Assert.Contains($"\"{name.ToUpperInvariant()}\"", failure.Message);
    }

    [Fact]
    public async Task Create_refuses_a_password_that_does_not_meet_the_policy()
    {
        var failure = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IUserDirectory>()
                .CreateAsync(Draft(NewName("weak"), "Weak Person", password: "indi")));

        Assert.False(failure.Succeeded);
        Assert.Contains($"at least {PasswordPolicy.MinimumLength} characters", failure.Message);
    }

    [Fact]
    public async Task Create_records_who_created_the_account_and_why()
    {
        var name = NewName("audited");

        var row = await _fixture.InScopeAsync(async services =>
        {
            var created = await CreateAsync(services, name, "Audited Person", reason: "Joined the counter on Monday.");

            var trail = await services.GetRequiredService<IAuditTrail>().SearchAsync(new AuditQuery
            {
                Action = AuditAction.UserCreated,
                EntityType = nameof(User),
                Term = created.UserName,
                Take = 5,
            });

            return trail.Single(r => r.EntityId == created.UserName);
        });

        Assert.Equal(Administrator, row.Actor);
        Assert.Equal("Joined the counter on Monday.", row.Reason);
        Assert.Equal($"Account \"{name}\" created with the {Role.Cashier.Label()} role.", row.Summary);
        Assert.Equal(nameof(User), row.EntityType);
    }

    [Fact]
    public async Task SetRole_moves_the_account_and_writes_the_move_onto_the_trail()
    {
        var name = NewName("moved");

        var row = await _fixture.InScopeAsync(async services =>
        {
            var created = await CreateAsync(services, name, "Moved Person");

            var changed = await services.GetRequiredService<IUserDirectory>()
                .SetRoleAsync(created.Id, Role.Pharmacist, "Qualified last week.");

            Assert.True(changed.Succeeded, changed.Message);
            Assert.Equal(Role.Pharmacist, changed.Value!.Role);

            var reread = await services.GetRequiredService<IUserDirectory>().FindAsync(created.Id);

            Assert.Equal(Role.Pharmacist, reread!.Role);

            return (await services.GetRequiredService<IAuditTrail>().SearchAsync(new AuditQuery
            {
                Action = AuditAction.UserRoleChanged,
                EntityType = nameof(User),
                Term = name,
                Take = 5,
            })).Single(r => r.EntityId == name);
        });

        Assert.Equal(Administrator, row.Actor);
        Assert.Equal("Qualified last week.", row.Reason);
        Assert.Equal($"Account \"{name}\" moved from {Role.Cashier.Label()} to {Role.Pharmacist.Label()}.", row.Summary);
        Assert.Equal($"Was {Role.Cashier}, now {Role.Pharmacist}.", row.Detail);
    }

    [Fact]
    public async Task SetRole_to_the_role_the_account_already_holds_changes_nothing_and_writes_nothing()
    {
        var name = NewName("same");

        var rows = await _fixture.InScopeAsync(async services =>
        {
            var created = await CreateAsync(services, name, "Unchanged Person");

            var changed = await services.GetRequiredService<IUserDirectory>().SetRoleAsync(created.Id, Role.Cashier);
            Assert.True(changed.Succeeded, changed.Message);

            return await services.GetRequiredService<IAuditTrail>().SearchAsync(new AuditQuery
            {
                Action = AuditAction.UserRoleChanged,
                Term = name,
                Take = 5,
            });
        });

        Assert.Empty(rows);
    }

    [Fact]
    public async Task SetRole_refuses_a_role_that_does_not_exist()
    {
        var failure = await _fixture.InScopeAsync(async services =>
        {
            var created = await CreateAsync(services, NewName("badrole"), "Bad Role");

            return await services.GetRequiredService<IUserDirectory>().SetRoleAsync(created.Id, (Role)9_999);
        });

        Assert.False(failure.Succeeded);
        Assert.Equal("'9999' is not a known role.", failure.Message);
    }

    [Fact]
    public async Task SetRole_refuses_an_account_that_no_longer_exists()
    {
        var failure = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IUserDirectory>().SetRoleAsync(987_654_321, Role.Cashier));

        Assert.False(failure.Succeeded);
        Assert.Equal("That account no longer exists.", failure.Message);
    }

    [Fact]
    public async Task SetRole_refuses_your_own_account()
    {
        // The permissions in force are read once at sign-in, so a role changed here would be recorded as
        // applied and would not be in force until the next sign-in.
        var failure = await _fixture.InScopeAsync(async services =>
        {
            var me = services.GetRequiredService<IUserSession>().CurrentUser!;

            return await services.GetRequiredService<IUserDirectory>().SetRoleAsync(me.Id, Role.Cashier);
        });

        Assert.False(failure.Succeeded);
        Assert.Contains("cannot change your own role", failure.Message);
    }

    [Fact]
    public async Task SetActive_refuses_to_deactivate_the_account_you_are_signed_in_with()
    {
        var failure = await _fixture.InScopeAsync(async services =>
        {
            var me = services.GetRequiredService<IUserSession>().CurrentUser!;

            return await services.GetRequiredService<IUserDirectory>().SetActiveAsync(me.Id, false);
        });

        Assert.False(failure.Succeeded);
        Assert.Contains("cannot deactivate the account you are signed in with", failure.Message);
    }

    [Fact]
    public async Task SetActive_deactivates_and_reactivates_an_account_carrying_the_reason_both_ways()
    {
        var name = NewName("onoff");

        var rows = await _fixture.InScopeAsync(async services =>
        {
            var directory = services.GetRequiredService<IUserDirectory>();
            var created = await CreateAsync(services, name, "On And Off");

            var stopped = await directory.SetActiveAsync(created.Id, false, "Suspended pending an enquiry.");
            Assert.True(stopped.Succeeded, stopped.Message);
            Assert.False(stopped.Value!.IsActive);

            var restarted = await directory.SetActiveAsync(created.Id, true, "The enquiry cleared them.");
            Assert.True(restarted.Succeeded, restarted.Message);
            Assert.True(restarted.Value!.IsActive);

            return await services.GetRequiredService<IAuditTrail>().SearchAsync(new AuditQuery
            {
                EntityType = nameof(User),
                Term = name,
                Take = 10,
            });
        });

        var stoppedRow = rows.Single(r => r.Action == AuditAction.UserDeactivated);
        var restartedRow = rows.Single(r => r.Action == AuditAction.UserReactivated);

        Assert.Equal("Suspended pending an enquiry.", stoppedRow.Reason);
        Assert.Equal($"Account \"{name}\" may no longer sign in.", stoppedRow.Summary);
        Assert.Equal("The enquiry cleared them.", restartedRow.Reason);
        Assert.Equal($"Account \"{name}\" may sign in again.", restartedRow.Summary);
    }

    [Fact]
    public async Task SetActive_reports_that_an_account_that_no_longer_exists_cannot_be_changed()
    {
        var failure = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IUserDirectory>().SetActiveAsync(987_654_321, false));

        Assert.False(failure.Succeeded);
        Assert.Equal("That account no longer exists.", failure.Message);
    }

    [Fact]
    public async Task ResetPassword_refuses_a_weak_password_and_leaves_the_old_one_working()
    {
        var name = NewName("weakreset");
        const string originalPassword = "Harbour-Lantern-63";

        var failure = await _fixture.InScopeAsync(async services =>
        {
            var created = await CreateAsync(services, name, "Weak Reset", originalPassword);

            return await services.GetRequiredService<IUserDirectory>()
                .ResetPasswordAsync(created.Id, "indi", "A hurried handover.");
        });

        Assert.False(failure.Succeeded);
        Assert.Contains($"at least {PasswordPolicy.MinimumLength} characters", failure.Message);

        var stillSignsIn = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                var signedIn = await services.GetRequiredService<IAuthenticationService>()
                    .SignInAsync(name, originalPassword);

                return signedIn.Succeeded;
            }
            finally
            {
                await SignInAsAdministratorAsync(services);
            }
        });

        Assert.True(stillSignsIn, "The password that was refused must not have replaced the working one.");
    }

    [Fact]
    public async Task ResetPassword_sets_a_password_the_owner_can_sign_in_with()
    {
        var name = NewName("reset");
        const string originalPassword = "Harbour-Lantern-63";
        const string newPassword = "Juniper-Trellis-58";

        var signedIn = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                var created = await CreateAsync(services, name, "Reset Person", originalPassword);

                var reset = await services.GetRequiredService<IUserDirectory>()
                    .ResetPasswordAsync(created.Id, newPassword, "Forgot it on the day of the stock take.");

                Assert.True(reset.Succeeded, reset.Message);

                var authentication = services.GetRequiredService<IAuthenticationService>();

                var withOld = await authentication.SignInAsync(name, originalPassword);
                Assert.False(withOld.Succeeded);

                var withNew = await authentication.SignInAsync(name, newPassword);
                Assert.True(withNew.Succeeded, withNew.Message);

                return withNew.User!.UserName;
            }
            finally
            {
                await SignInAsAdministratorAsync(services);
            }
        });

        Assert.Equal(name, signedIn);

        // The owner cannot read the row that was written about them; the administrator who wrote it can.
        var row = await _fixture.InScopeAsync(async services =>
            (await services.GetRequiredService<IAuditTrail>().SearchAsync(new AuditQuery
            {
                Action = AuditAction.PasswordReset,
                Term = name,
                Take = 5,
            })).Single(entry => entry.EntityId == name));

        Assert.Equal(Administrator, row.Actor);
        Assert.Equal("Forgot it on the day of the stock take.", row.Reason);

        // The password itself is never written down, not even as a shape.
        Assert.DoesNotContain(newPassword, row.Summary);
    }

    [Fact]
    public async Task ResetPassword_refuses_your_own_account()
    {
        var failure = await _fixture.InScopeAsync(async services =>
        {
            var me = services.GetRequiredService<IUserSession>().CurrentUser!;

            return await services.GetRequiredService<IUserDirectory>()
                .ResetPasswordAsync(me.Id, "Juniper-Trellis-58", "Testing the guard.");
        });

        Assert.False(failure.Succeeded);
        Assert.Contains("cannot set their own password from here", failure.Message);
    }

    [Fact]
    public async Task Every_method_is_refused_for_an_operator_who_may_not_manage_users()
    {
        // The settings screen is not offered to a cashier. This is the check that matters: the same refusal has
        // to come from the catalogue when nothing hid the button and nothing greys it out.
        var refusals = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                const string password = "Juniper-Trellis-58";
                var cashier = await CreateAsync(services, NewName("nobody"), "No Permissions", password);

                var authentication = services.GetRequiredService<IAuthenticationService>();
                var signedIn = await authentication.SignInAsync(cashier.UserName, password);
                Assert.True(signedIn.Succeeded, signedIn.Message);

                var directory = services.GetRequiredService<IUserDirectory>();
                var messages = new List<string>();

                await Record(messages, () => directory.SearchAsync(new UserQuery()));
                await Record(messages, () => directory.FindAsync(cashier.Id));
                await Record(messages, () => directory.CountAsync());
                await Record(messages, () => directory.CreateAsync(Draft(NewName("denied"), "Denied Person")));
                await Record(messages, () => directory.SetRoleAsync(cashier.Id, Role.Pharmacist));
                await Record(messages, () => directory.SetActiveAsync(cashier.Id, false));
                await Record(messages, () => directory.ResetPasswordAsync(cashier.Id, password));

                return messages;
            }
            finally
            {
                await SignInAsAdministratorAsync(services);
            }
        });

        Assert.Equal(7, refusals.Count);

        foreach (var message in refusals)
        {
            Assert.Contains("An administrator can grant it", message);
            Assert.Contains($"permission: {Permission.ManageUsers}", message);
        }
    }

    private static async Task Record(List<string> refusals, Func<Task> call)
    {
        var thrown = await Assert.ThrowsAsync<PermissionDeniedException>(call);
        refusals.Add(thrown.Message);
    }

    private static async Task<UserDto> CreateAsync(
        IServiceProvider services,
        string userName,
        string displayName,
        string password = "Marigold-Till-92",
        string? reason = null)
    {
        var result = await services.GetRequiredService<IUserDirectory>()
            .CreateAsync(Draft(userName, displayName, password) with { Reason = reason });

        Assert.True(result.Succeeded, $"Could not create {userName}: {result.Message}");
        return result.Value!;
    }

    /// <summary>
    /// Signs the administrator back in. Every test that signs somebody else in, or that changes the password of
    /// an account it is about to sign in as, has to call this before it returns: the whole run shares one
    /// session, and the rest of it acts as the administrator.
    /// </summary>
    private static async Task SignInAsAdministratorAsync(IServiceProvider services)
    {
        var signedIn = await services.GetRequiredService<IAuthenticationService>()
            .SignInAsync(Administrator, TestDatabaseFixture.AdministratorPassword);

        Assert.True(signedIn.Succeeded, $"Could not hand the session back to the administrator: {signedIn.Message}");
    }

    private static UserDraft Draft(string userName, string displayName, string password = "Marigold-Till-92") => new()
    {
        UserName = userName,
        DisplayName = displayName,
        Role = Role.Cashier,
        Password = password,
        ConfirmPassword = password,
    };

    private static string NewName(string stem) => $"it.{stem}.{Stamp()}";

    private static string Stamp() => Guid.NewGuid().ToString("N")[..8];
}
