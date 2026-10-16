namespace MediStock.Domain.Identity;

/// <summary>
/// A job in the pharmacy. The set is small on purpose: four roles are enough to separate who may sell,
/// who may change a price, and who may touch the database, without turning into a permission matrix an
/// operator cannot reason about.
/// </summary>
public enum Role
{
    /// <summary>Owns the installation. Everything, including users and backups.</summary>
    Administrator,

    /// <summary>Dispenses, takes returns, prices the shelf and receives stock.</summary>
    Pharmacist,

    /// <summary>Rings up sales. Nothing else.</summary>
    Cashier,

    /// <summary>Keeps the catalogue, the suppliers and the store room in order.</summary>
    InventoryOperator,
}
