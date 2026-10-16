namespace MediStock.Domain.Identity;

/// <summary>
/// A single thing a signed-in person is allowed to do. Permissions are deliberately coarse and named
/// after a business activity rather than a screen, so a role can be described in the language the
/// pharmacy uses ("can take a return") instead of the language the interface happens to be built in.
/// </summary>
public enum Permission
{
    /// <summary>Ring up a sale and take payment.</summary>
    Sell,

    /// <summary>Take medicine back against an invoice and refund it.</summary>
    ProcessReturns,

    /// <summary>Create and edit medicines in the catalogue.</summary>
    ManageMedicines,

    /// <summary>
    /// Change a selling price or a tax rate. Kept apart from <see cref="ManageMedicines"/>, because a
    /// store-room operator who may correct a spelling must not thereby reprice the shelf.
    /// </summary>
    ChangePrices,

    /// <summary>Record a supplier invoice and receive its goods into stock.</summary>
    ReceivePurchases,

    /// <summary>Create and edit suppliers.</summary>
    ManageSuppliers,

    /// <summary>Run and export the operational reports.</summary>
    RunReports,

    /// <summary>Create, deactivate and re-role other users.</summary>
    ManageUsers,

    /// <summary>Take and restore database backups.</summary>
    BackupDatabase,

    /// <summary>Read the audit trail.</summary>
    ViewAuditTrail,
}
