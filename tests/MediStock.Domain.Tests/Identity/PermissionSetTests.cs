using MediStock.Domain.Identity;

namespace MediStock.Domain.Tests.Identity;

/// <summary>
/// The set of permissions is the only thing that decides a refusal, so the way it answers and the way it
/// words that refusal are both worth pinning down.
/// </summary>
public class PermissionSetTests
{
    [Fact]
    public void Empty_grants_nothing_and_reads_as_nothing()
    {
        var permissions = PermissionSet.Empty;

        Assert.Equal(0, permissions.Count);
        Assert.Empty(permissions.Permissions);
        Assert.False(permissions.Has(Permission.Sell));
        Assert.Equal("nothing", permissions.Describe());
    }

    [Fact]
    public void All_grants_every_permission_the_application_defines()
    {
        // Enumerates the enum rather than listing the members: a permission added later is covered by All
        // without anyone remembering to extend the administrator's role by hand.
        foreach (var permission in Enum.GetValues<Permission>())
        {
            Assert.True(PermissionSet.All.Has(permission), $"All does not grant {permission}.");
        }

        Assert.Equal(Enum.GetValues<Permission>().Length, PermissionSet.All.Count);
    }

    [Fact]
    public void Of_ignores_a_repeated_permission()
    {
        var permissions = PermissionSet.Of([Permission.Sell, Permission.Sell, Permission.RunReports]);

        Assert.Equal(2, permissions.Count);
    }

    [Fact]
    public void Of_given_nothing_returns_the_empty_set()
    {
        // The same instance, so an empty set built from a filtered list does not allocate a second time.
        Assert.Same(PermissionSet.Empty, PermissionSet.Of([]));
    }

    [Fact]
    public void Permissions_are_listed_in_the_order_they_are_declared()
    {
        var permissions = PermissionSet.Of(
            [Permission.ViewAuditTrail, Permission.Sell, Permission.ManageUsers]);

        Assert.Equal([Permission.Sell, Permission.ManageUsers, Permission.ViewAuditTrail], permissions.Permissions);
    }

    [Fact]
    public void HasAll_needs_every_permission()
    {
        var permissions = PermissionSet.Of([Permission.Sell, Permission.ProcessReturns]);

        Assert.True(permissions.HasAll(Permission.Sell, Permission.ProcessReturns));
        Assert.False(permissions.HasAll(Permission.Sell, Permission.ManageUsers));
        Assert.True(permissions.HasAll());
    }

    [Fact]
    public void Require_permits_a_granted_permission_and_throws_for_a_missing_one()
    {
        var permissions = PermissionSet.Of([Permission.Sell]);

        permissions.Require(Permission.Sell, "sell this");

        var refused = Assert.Throws<PermissionDeniedException>(
            () => permissions.Require(Permission.BackupDatabase, "take a backup"));

        Assert.Equal(
            "You do not have permission to take a backup. An administrator can grant it (permission: BackupDatabase).",
            refused.Message);
    }

    [Fact]
    public void Explain_falls_back_to_the_permission_label_when_the_action_is_not_given()
    {
        Assert.Equal(
            "You do not have permission to change prices. An administrator can grant it (permission: ChangePrices).",
            PermissionSet.Explain(Permission.ChangePrices));

        // A blank action is treated as one that was not given, rather than producing "permission to .".
        Assert.Equal(
            PermissionSet.Explain(Permission.ChangePrices),
            PermissionSet.Explain(Permission.ChangePrices, "   "));
    }

    [Fact]
    public void A_refusal_names_the_action_and_the_permission_but_never_the_role()
    {
        var message = PermissionSet.Explain(Permission.ManageUsers, "open this account");

        Assert.Contains("open this account", message, StringComparison.Ordinal);
        Assert.Contains("ManageUsers", message, StringComparison.Ordinal);

        // The message says an administrator can put it right, which is the one role it names on purpose.
        // Every other role is left out: knowing whether the operator is a cashier or a pharmacist would not
        // help them get the work done, and reading a role out over a shared counter is its own disclosure.
        foreach (var role in RoleCatalog.All.Where(r => r != Role.Administrator))
        {
            Assert.DoesNotContain(role.Label(), message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Describe_lists_what_the_set_carries_in_lower_case()
    {
        var permissions = PermissionSet.Of([Permission.Sell, Permission.ProcessReturns]);

        Assert.Equal("sell, take returns", permissions.Describe());
    }

    [Fact]
    public void PermissionDenied_is_a_domain_exception_so_a_screen_can_show_its_message_as_it_stands()
    {
        var refused = new PermissionDeniedException("no");

        Assert.IsAssignableFrom<MediStock.Domain.Common.DomainException>(refused);
    }
}
