using MediStock.Application.Identity;
using MediStock.Domain.Common;
using MediStock.Domain.Identity;

namespace MediStock.Application.Tests.Identity;

/// <summary>
/// The session answers "who is signed in", and is the only thing the screens ask. Its two easy mistakes are
/// announcing a change nobody can act on, and forgetting to announce one that everybody has to act on.
/// </summary>
public sealed class UserSessionTests
{
    private static UserDto Person(
        int id = 1,
        string userName = "anita",
        string displayName = "Anita Rao",
        Role role = Role.Cashier) =>
        new(id, userName, displayName, role, true, DateTimeOffset.UnixEpoch, null);

    [Fact]
    public void Before_anybody_signs_in_the_session_grants_nothing()
    {
        var session = new UserSession();

        Assert.False(session.IsSignedIn);
        Assert.Null(session.CurrentUser);
        Assert.False(session.IsAdministrator);
        Assert.Equal(0, session.Permissions.Count);
        Assert.False(session.Can(Permission.Sell));
    }

    [Fact]
    public void Requiring_a_permission_with_nobody_signed_in_refuses()
    {
        var session = new UserSession();

        Assert.Throws<PermissionDeniedException>(() => session.Require(Permission.Sell, "take a payment"));
    }

    [Fact]
    public void Adopting_a_person_signs_them_in_and_announces_it_once()
    {
        var session = new UserSession();
        var announcements = 0;
        session.Changed += (_, _) => announcements++;

        session.Adopt(Person(role: Role.Cashier));

        Assert.Equal(1, announcements);
        Assert.True(session.IsSignedIn);
        Assert.Equal("Anita Rao", session.CurrentUser!.FriendlyName);
        Assert.True(session.Can(Permission.Sell));
        Assert.False(session.Can(Permission.ProcessReturns));
        Assert.False(session.IsAdministrator);
    }

    [Fact]
    public void An_administrator_holds_every_permission()
    {
        var session = new UserSession();

        session.Adopt(Person(role: Role.Administrator));

        Assert.True(session.IsAdministrator);
        Assert.Equal(Enum.GetValues<Permission>().Length, session.Permissions.Count);

        foreach (var permission in Enum.GetValues<Permission>())
        {
            Assert.True(session.Can(permission), $"An administrator should be able to {permission}.");
            session.Require(permission);
        }
    }

    [Fact]
    public void Clearing_takes_the_permissions_away_and_announces_it_once()
    {
        var session = new UserSession();
        session.Adopt(Person(role: Role.Administrator));
        var announcements = 0;
        session.Changed += (_, _) => announcements++;

        session.Clear();

        Assert.Equal(1, announcements);
        Assert.False(session.IsSignedIn);
        Assert.Equal(0, session.Permissions.Count);
    }

    [Fact]
    public void Clearing_an_empty_session_says_nothing()
    {
        var session = new UserSession();
        var announcements = 0;
        session.Changed += (_, _) => announcements++;

        session.Clear();

        Assert.Equal(0, announcements);
    }

    [Fact]
    public void A_refresh_that_changes_nothing_says_nothing()
    {
        var person = Person();
        var session = new UserSession();
        session.Adopt(person);
        var announcements = 0;
        session.Changed += (_, _) => announcements++;

        session.Refresh(person);

        Assert.Equal(0, announcements);
    }

    [Fact]
    public void A_refresh_that_changes_the_role_is_announced_and_applied()
    {
        var session = new UserSession();
        session.Adopt(Person(role: Role.Cashier));
        var announcements = 0;
        session.Changed += (_, _) => announcements++;

        session.Refresh(Person(role: Role.Pharmacist));

        Assert.Equal(1, announcements);
        Assert.True(session.Can(Permission.ProcessReturns));
    }

    [Fact]
    public void A_refresh_for_somebody_else_is_ignored()
    {
        // The settings screen edits other people. Their details must not be published as the operator's own.
        var session = new UserSession();
        session.Adopt(Person(id: 1));
        var announcements = 0;
        session.Changed += (_, _) => announcements++;

        session.Refresh(Person(id: 2, userName: "sam", displayName: "Sam", role: Role.Administrator));

        Assert.Equal(0, announcements);
        Assert.Equal(1, session.CurrentUser!.Id);
        Assert.False(session.IsAdministrator);
    }

    [Fact]
    public void A_refresh_with_nobody_signed_in_is_ignored()
    {
        var session = new UserSession();
        var announcements = 0;
        session.Changed += (_, _) => announcements++;

        session.Refresh(Person());

        Assert.Equal(0, announcements);
        Assert.False(session.IsSignedIn);
    }

    [Fact]
    public void Adopting_or_refreshing_a_null_is_refused()
    {
        var session = new UserSession();

        Assert.Throws<ArgumentNullException>(() => session.Adopt(null!));
        Assert.Throws<ArgumentNullException>(() => session.Refresh(null!));
    }

    [Fact]
    public void A_refusal_names_the_action_the_operator_asked_for()
    {
        var session = new UserSession();
        session.Adopt(Person(role: Role.Cashier));

        var thrown = Assert.Throws<PermissionDeniedException>(
            () => session.Require(Permission.BackupDatabase, "take a backup of the database"));

        Assert.Contains("take a backup of the database", thrown.Message);
        Assert.Contains(nameof(Permission.BackupDatabase), thrown.Message);
    }
}
