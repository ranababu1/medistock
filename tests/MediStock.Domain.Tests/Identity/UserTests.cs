using MediStock.Domain.Common;
using MediStock.Domain.Identity;

namespace MediStock.Domain.Tests.Identity;

/// <summary>
/// The account aggregate. The rules worth protecting are the two that keep a pharmacy able to administer
/// itself — never losing the last administrator — and the one that keeps the sign-in prompt from telling a
/// stranger which account names exist.
/// </summary>
public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 5, 0, 0, TimeSpan.Zero);

    /// <summary>A cheap work factor, so a test that creates several accounts still finishes instantly.</summary>
    private static PasswordHash Password(string password = "Kestrel-Harbour-71")
        => PasswordHash.Create(password, PasswordHash.MinimumIterations);

    private static User Create(
        string userName = "imran",
        string displayName = "Imran Khan",
        Role role = Role.Pharmacist)
        => User.Create(userName, displayName, role, Password(), Now);

    [Fact]
    public void A_new_account_is_active_has_never_signed_in_and_carries_its_role_permissions()
    {
        var user = Create(role: Role.Cashier);

        Assert.True(user.IsActive);
        Assert.Null(user.LastSignedInAtUtc);
        Assert.Equal(Now, user.CreatedAtUtc);
        Assert.Equal(Now, user.UpdatedAtUtc);
        Assert.Equal(Role.Cashier, user.Role);

        Assert.True(user.Can(Permission.Sell));
        Assert.False(user.Can(Permission.ManageUsers));
    }

    [Fact]
    public void The_account_name_is_folded_for_comparison_so_two_spellings_cannot_both_exist()
    {
        var user = Create(userName: "  Imran  ");

        Assert.Equal("Imran", user.UserName);
        Assert.Equal("imran", user.NormalisedUserName);
    }

    [Fact]
    public void The_stored_password_checks_the_password_it_was_created_from_and_nothing_else()
    {
        var user = Create();

        Assert.True(user.MatchesPassword("Kestrel-Harbour-71"));
        Assert.False(user.MatchesPassword("Kestrel-Harbour-72"));
        Assert.False(user.MatchesPassword(null));
    }

    [Fact]
    public void An_inactive_account_still_answers_the_password_question_so_the_caller_can_choose_the_wording()
    {
        // The sign-in service needs the two answers to be separable, so that it can check the password first
        // and then decide what to tell the operator — and tell them the same thing either way.
        var user = Create();
        user.Deactivate(otherActiveAdministrators: 1, nowUtc: Now);

        Assert.False(user.IsActive);
        Assert.True(user.MatchesPassword("Kestrel-Harbour-71"));
    }

    [Fact]
    public void An_account_whose_stored_password_is_damaged_refuses_every_password_instead_of_throwing()
    {
        var user = Create();

        // Stands in for a row somebody edited by hand.
        typeof(User)
            .GetProperty(nameof(User.PasswordHashEncoded))!
            .SetValue(user, "not a hash");

        Assert.False(user.MatchesPassword("Kestrel-Harbour-71"));
        Assert.False(user.MatchesPassword("anything"));
    }

    [Fact]
    public void Creating_an_account_refuses_a_blank_account_name_or_display_name()
    {
        Assert.Throws<DomainValidationException>(() => Create(userName: "   "));
        Assert.Throws<DomainValidationException>(() => Create(displayName: "   "));
    }

    [Fact]
    public void Creating_an_account_refuses_a_name_longer_than_the_column()
    {
        var tooLong = new string('a', User.UserNameMaxLength + 1);
        Assert.Throws<DomainValidationException>(() => Create(userName: tooLong));

        var displayTooLong = new string('a', User.DisplayNameMaxLength + 1);
        Assert.Throws<DomainValidationException>(() => Create(displayName: displayTooLong));
    }

    [Fact]
    public void Creating_an_account_refuses_a_role_the_application_does_not_define()
    {
        Assert.Throws<DomainValidationException>(
            () => User.Create("imran", "Imran Khan", (Role)99, Password(), Now));
    }

    [Fact]
    public void Renaming_the_account_refolds_the_name_and_moves_the_update_time()
    {
        var user = Create();
        var later = Now.AddHours(1);

        user.Rename("  Imran.K  ", later);

        Assert.Equal("Imran.K", user.UserName);
        Assert.Equal("imran.k", user.NormalisedUserName);
        Assert.Equal(Now, user.CreatedAtUtc);
        Assert.Equal(later, user.UpdatedAtUtc);
    }

    [Fact]
    public void The_display_name_can_be_changed_without_touching_the_account_name()
    {
        var user = Create();
        var later = Now.AddHours(1);

        user.SetDisplayName("Imran A Khan", later);

        Assert.Equal("Imran A Khan", user.DisplayName);
        Assert.Equal("imran", user.UserName);
        Assert.Equal(later, user.UpdatedAtUtc);
    }

    [Fact]
    public void A_display_name_is_required_so_the_top_bar_is_never_blank()
    {
        // FriendlyName falls back to the account name, but the fallback is only reachable for a row written
        // by an older release: every path in this one refuses a blank display name outright.
        var refused = Assert.Throws<DomainValidationException>(() => Create(displayName: "   "));

        Assert.Contains("Display name", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_screen_calls_a_person_is_the_display_name()
    {
        var user = Create(userName: "imran", displayName: "Imran Khan");

        Assert.Equal("Imran Khan", user.FriendlyName);
    }

    [Fact]
    public void Setting_a_new_password_replaces_the_old_one()
    {
        var user = Create();
        user.SetPassword(Password("Fennel-Juniper-38"), Now.AddDays(1));

        Assert.True(user.MatchesPassword("Fennel-Juniper-38"));
        Assert.False(user.MatchesPassword("Kestrel-Harbour-71"));
    }

    [Fact]
    public void Changing_a_role_needs_no_spare_administrator_when_the_new_role_is_not_administrator()
    {
        var user = Create();
        var later = Now.AddHours(1);

        user.ChangeRole(Role.InventoryOperator, otherActiveAdministrators: 0, nowUtc: later);

        Assert.Equal(Role.InventoryOperator, user.Role);
        Assert.Equal(later, user.UpdatedAtUtc);
    }

    [Fact]
    public void Changing_a_role_to_the_one_already_held_changes_nothing_at_all()
    {
        var user = Create();

        user.ChangeRole(Role.Pharmacist, otherActiveAdministrators: 0, nowUtc: Now.AddDays(1));

        Assert.Equal(Now, user.UpdatedAtUtc);
    }

    [Fact]
    public void The_last_active_administrator_cannot_be_demoted()
    {
        var user = Create(role: Role.Administrator);

        var refused = Assert.Throws<DomainValidationException>(
            () => user.ChangeRole(Role.Pharmacist, otherActiveAdministrators: 0));

        Assert.Contains("only active administrator", refused.Message, StringComparison.Ordinal);

        // And the refusal left the account as it was.
        Assert.Equal(Role.Administrator, user.Role);
        Assert.Equal(Now, user.UpdatedAtUtc);
    }

    [Fact]
    public void The_last_active_administrator_cannot_be_deactivated()
    {
        var user = Create(role: Role.Administrator);

        var refused = Assert.Throws<DomainValidationException>(
            () => user.Deactivate(otherActiveAdministrators: 0));

        Assert.Contains("only active administrator", refused.Message, StringComparison.Ordinal);
        Assert.True(user.IsActive);
    }

    [Fact]
    public void An_administrator_with_a_spare_can_be_demoted_and_then_deactivated()
    {
        var user = Create(role: Role.Administrator);

        user.ChangeRole(Role.Cashier, otherActiveAdministrators: 1);
        Assert.Equal(Role.Cashier, user.Role);

        // Demoted already, so the spare no longer matters: this is not the last administrator any more.
        user.Deactivate(otherActiveAdministrators: 0);
        Assert.False(user.IsActive);
    }

    [Fact]
    public void Deactivating_an_account_that_is_already_inactive_changes_nothing()
    {
        var user = Create();

        user.Deactivate(otherActiveAdministrators: 0, nowUtc: Now);
        user.Deactivate(otherActiveAdministrators: 0, nowUtc: Now.AddDays(5));

        Assert.False(user.IsActive);
        Assert.Equal(Now, user.UpdatedAtUtc);
    }

    [Fact]
    public void Reactivating_an_account_that_is_already_active_changes_nothing()
    {
        var user = Create();

        user.Reactivate(Now.AddDays(5));

        Assert.Equal(Now, user.UpdatedAtUtc);
    }

    [Fact]
    public void A_deactivated_account_can_be_brought_back()
    {
        var user = Create();

        user.Deactivate(otherActiveAdministrators: 0, nowUtc: Now.AddDays(1));
        Assert.False(user.IsActive);

        user.Reactivate(Now.AddDays(2));

        Assert.True(user.IsActive);
        Assert.Equal(Now.AddDays(2), user.UpdatedAtUtc);
    }

    [Fact]
    public void Recording_a_sign_in_is_not_the_same_as_reactivating_the_account()
    {
        var user = Create();
        var later = Now.AddDays(1);

        user.RecordSignIn(later);

        Assert.Equal(later, user.LastSignedInAtUtc);
        Assert.Equal(later, user.UpdatedAtUtc);
    }

    [Fact]
    public void The_permissions_in_force_are_read_from_the_role_each_time_it_is_asked()
    {
        var user = Create(role: Role.Cashier);
        Assert.False(user.Can(Permission.ManageMedicines));

        user.ChangeRole(Role.InventoryOperator, otherActiveAdministrators: 0);

        Assert.True(user.Can(Permission.ManageMedicines));
        Assert.False(user.Can(Permission.Sell));
    }
}
