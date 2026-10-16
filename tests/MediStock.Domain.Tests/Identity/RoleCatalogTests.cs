using MediStock.Domain.Identity;

namespace MediStock.Domain.Tests.Identity;

/// <summary>
/// The role-to-permission map is a business decision, not a lookup table, so it is asserted here in the same
/// place a reader would look for it. Each test names the pharmacy rule it protects rather than merely
/// restating the table.
/// </summary>
public class RoleCatalogTests
{
    [Fact]
    public void Every_role_has_a_permission_set()
    {
        foreach (var role in Enum.GetValues<Role>())
        {
            var permissions = RoleCatalog.PermissionsFor(role);

            Assert.NotNull(permissions);

            // A role that carries nothing is a mistake rather than a decision: it would sign somebody in who
            // could do no work at all, with no way for them to tell why.
            Assert.True(permissions.Count > 0, $"{role} carries no permissions at all.");
        }
    }

    [Fact]
    public void All_is_listed_in_the_order_the_role_picker_offers_them()
    {
        Assert.Equal(
            [Role.Administrator, Role.Pharmacist, Role.Cashier, Role.InventoryOperator],
            RoleCatalog.All);

        Assert.Equal(Enum.GetValues<Role>().Length, RoleCatalog.All.Count);
    }

    [Fact]
    public void The_administrator_holds_every_permission()
    {
        foreach (var permission in Enum.GetValues<Permission>())
        {
            Assert.True(
                RoleCatalog.PermissionsFor(Role.Administrator).Has(permission),
                $"The administrator cannot {permission}.");
        }
    }

    [Fact]
    public void A_cashier_may_only_ring_up_a_sale()
    {
        var cashier = RoleCatalog.PermissionsFor(Role.Cashier);

        Assert.True(cashier.Has(Permission.Sell));
        Assert.Equal(1, cashier.Count);

        // Daily takings are a management figure; the plan keeps reporting away from the till.
        Assert.False(cashier.Has(Permission.RunReports));

        // And the store room is somebody else's job.
        Assert.False(cashier.Has(Permission.ReceivePurchases));
        Assert.False(cashier.Has(Permission.ManageMedicines));
    }

    [Fact]
    public void A_pharmacist_prices_the_shelf_and_takes_returns_but_does_not_administer()
    {
        var pharmacist = RoleCatalog.PermissionsFor(Role.Pharmacist);

        Assert.True(pharmacist.Has(Permission.Sell));
        Assert.True(pharmacist.Has(Permission.ProcessReturns));
        Assert.True(pharmacist.Has(Permission.ChangePrices));
        Assert.True(pharmacist.Has(Permission.ReceivePurchases));

        Assert.False(pharmacist.Has(Permission.ManageUsers));
        Assert.False(pharmacist.Has(Permission.BackupDatabase));
        Assert.False(pharmacist.Has(Permission.ViewAuditTrail));
    }

    [Fact]
    public void An_inventory_operator_keeps_the_catalogue_but_may_not_touch_a_price_or_the_till()
    {
        var storeRoom = RoleCatalog.PermissionsFor(Role.InventoryOperator);

        Assert.True(storeRoom.Has(Permission.ManageMedicines));
        Assert.True(storeRoom.Has(Permission.ManageSuppliers));
        Assert.True(storeRoom.Has(Permission.ReceivePurchases));

        // Somebody who may correct a spelling must not thereby reprice the shelf.
        Assert.False(storeRoom.Has(Permission.ChangePrices));
        Assert.False(storeRoom.Has(Permission.Sell));
    }

    [Fact]
    public void Only_the_administrator_may_administer_users_take_a_backup_or_read_the_trail()
    {
        foreach (var role in RoleCatalog.All.Where(r => r != Role.Administrator))
        {
            var permissions = RoleCatalog.PermissionsFor(role);

            Assert.False(permissions.Has(Permission.ManageUsers), $"{role} may manage users.");
            Assert.False(permissions.Has(Permission.BackupDatabase), $"{role} may take a backup.");
            Assert.False(permissions.Has(Permission.ViewAuditTrail), $"{role} may read the trail.");
        }
    }

    [Fact]
    public void A_role_that_may_ring_up_a_sale_is_not_thereby_trusted_to_take_medicine_back()
    {
        // A return moves stock and money and has to reference the sale it reverses, so it sits behind its
        // own permission rather than following from Sell. A cashier therefore has to hand a return to a
        // pharmacist. That is a decision, not an oversight, and this test exists so that widening the
        // cashier's role is something somebody chooses on purpose.
        var cashier = RoleCatalog.PermissionsFor(Role.Cashier);

        Assert.True(cashier.Has(Permission.Sell));
        Assert.False(cashier.Has(Permission.ProcessReturns));

        // The pharmacist, who is accountable for the shelf, can do both.
        var pharmacist = RoleCatalog.PermissionsFor(Role.Pharmacist);

        Assert.True(pharmacist.Has(Permission.Sell));
        Assert.True(pharmacist.Has(Permission.ProcessReturns));
    }

    [Theory]
    [InlineData("Administrator", Role.Administrator)]
    [InlineData("administrator", Role.Administrator)]
    [InlineData("Inventory operator", Role.InventoryOperator)]
    [InlineData("inventoryoperator", Role.InventoryOperator)]
    [InlineData("  Cashier  ", Role.Cashier)]
    [InlineData("Pharmacist", Role.Pharmacist)]
    public void TryParse_reads_back_either_the_label_an_operator_sees_or_the_declared_name(
        string stored,
        Role expected)
    {
        Assert.True(RoleCatalog.TryParse(stored, out var role));
        Assert.Equal(expected, role);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Wizard")]
    [InlineData("4")]
    public void TryParse_refuses_a_value_that_is_not_a_role(string? stored)
    {
        Assert.False(RoleCatalog.TryParse(stored, out _));
    }

    [Fact]
    public void Every_role_round_trips_through_its_declared_name()
    {
        // A role stored by an older release has to keep parsing after a label is reworded.
        foreach (var role in RoleCatalog.All)
        {
            Assert.True(RoleCatalog.TryParse(role.ToString(), out var parsed));
            Assert.Equal(role, parsed);
        }
    }
}
