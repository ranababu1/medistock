namespace MediStock.Domain.Identity;

/// <summary>
/// The things worth recording about who did what. Kept as a closed list rather than free text so that a
/// report over the audit trail can group by action, and so a new sensitive act has to be named before it
/// can be logged.
/// </summary>
public enum AuditAction
{
    /// <summary>Somebody signed in with the right password.</summary>
    SignInSucceeded,

    /// <summary>A sign-in was attempted and refused: unknown account, wrong password or inactive account.</summary>
    SignInFailed,

    /// <summary>An account signed out, or the application closed while it was signed in.</summary>
    SignOut,

    /// <summary>An account chose a new password for itself.</summary>
    PasswordChanged,

    /// <summary>An administrator set somebody else's password.</summary>
    PasswordReset,

    /// <summary>An account was created.</summary>
    UserCreated,

    /// <summary>An account was stopped from signing in.</summary>
    UserDeactivated,

    /// <summary>An account was allowed to sign in again.</summary>
    UserReactivated,

    /// <summary>An account was moved to another role.</summary>
    UserRoleChanged,

    /// <summary>A medicine was added to the catalogue.</summary>
    MedicineCreated,

    /// <summary>A medicine's details were edited.</summary>
    MedicineUpdated,

    /// <summary>A selling price or a tax rate changed, recorded separately from an ordinary edit.</summary>
    PriceChanged,

    /// <summary>A medicine was withdrawn from the catalogue without deleting its history.</summary>
    MedicineDeactivated,

    /// <summary>A medicine was put back into the catalogue.</summary>
    MedicineReactivated,

    /// <summary>A supplier was added.</summary>
    SupplierCreated,

    /// <summary>A supplier's details were edited.</summary>
    SupplierUpdated,

    /// <summary>A supplier was stopped from being used for new purchases.</summary>
    SupplierDeactivated,

    /// <summary>A supplier was put back into use.</summary>
    SupplierReactivated,

    /// <summary>A supplier invoice was recorded, not yet received.</summary>
    PurchaseCreated,

    /// <summary>A recorded supplier invoice was edited before receiving.</summary>
    PurchaseUpdated,

    /// <summary>Goods were received into stock and batches were created.</summary>
    PurchaseReceived,

    /// <summary>A receipt was reversed and its stock movements undone by new movements.</summary>
    PurchaseReversed,

    /// <summary>A recorded invoice was cancelled before receiving.</summary>
    PurchaseCancelled,

    /// <summary>A sale was checked out and its stock movements written.</summary>
    SaleCompleted,

    /// <summary>A completed sale was cancelled and its stock returned to the batches it came from.</summary>
    SaleCancelled,

    /// <summary>Medicine was taken back against an invoice.</summary>
    ReturnRecorded,

    /// <summary>A batch balance was changed by hand, without a sale or a purchase explaining it.</summary>
    StockAdjusted,

    /// <summary>Expired or recalled stock was disposed of and its quantity left the ledger.</summary>
    StockWrittenOff,

    /// <summary>Stock was sent back to its supplier and the credit recorded.</summary>
    SupplierReturnRecorded,

    /// <summary>A database backup was taken.</summary>
    BackupTaken,

    /// <summary>A backup was restored over the live database.</summary>
    RestoreCompleted,
}
