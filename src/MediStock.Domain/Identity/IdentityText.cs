namespace MediStock.Domain.Identity;

/// <summary>
/// The words the application uses for roles and permissions. They live in the domain rather than the
/// interface so that a refusal, an audit row and a settings screen all name the same thing the same way.
/// </summary>
public static class IdentityText
{
    /// <summary>The permission as a short phrase an operator would recognise, for a list or a message.</summary>
    public static string Label(this Permission permission) => permission switch
    {
        Permission.Sell => "Sell",
        Permission.ProcessReturns => "Take returns",
        Permission.ManageMedicines => "Manage the medicine catalogue",
        Permission.ChangePrices => "Change prices",
        Permission.ReceivePurchases => "Receive purchases",
        Permission.ManageSuppliers => "Manage suppliers",
        Permission.RunReports => "Run reports",
        Permission.ManageUsers => "Manage users",
        Permission.BackupDatabase => "Back up the database",
        Permission.ViewAuditTrail => "Read the audit trail",
        _ => permission.ToString(),
    };

    /// <summary>The longer description of a permission, for a tooltip or a settings list.</summary>
    public static string Describe(this Permission permission) => permission switch
    {
        Permission.Sell => "Ring up a sale and take payment.",
        Permission.ProcessReturns => "Take medicine back against an invoice and refund it.",
        Permission.ManageMedicines => "Create and edit medicines in the catalogue.",
        Permission.ChangePrices => "Change a selling price or a tax rate.",
        Permission.ReceivePurchases => "Record a supplier invoice and receive its goods into stock.",
        Permission.ManageSuppliers => "Create and edit suppliers.",
        Permission.RunReports => "Run and export the operational reports.",
        Permission.ManageUsers => "Create, deactivate and re-role other users.",
        Permission.BackupDatabase => "Take and restore database backups.",
        Permission.ViewAuditTrail => "Read the audit trail.",
        _ => permission.Label(),
    };

    /// <summary>The role as it is written on screen.</summary>
    public static string Label(this Role role) => role switch
    {
        Role.Administrator => "Administrator",
        Role.Pharmacist => "Pharmacist",
        Role.Cashier => "Cashier",
        Role.InventoryOperator => "Inventory operator",
        _ => role.ToString(),
    };

    /// <summary>What the role is for, in one line, for a role picker.</summary>
    public static string Describe(this Role role) => role switch
    {
        Role.Administrator => "Full access, including users, backups and the audit trail.",
        Role.Pharmacist => "Sells, takes returns, prices the shelf and receives stock.",
        Role.Cashier => "Rings up sales only.",
        Role.InventoryOperator => "Keeps the catalogue, suppliers and store room in order.",
        _ => role.Label(),
    };
}
