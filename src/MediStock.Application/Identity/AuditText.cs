using MediStock.Domain.Identity;

namespace MediStock.Application.Identity;

/// <summary>
/// The words the audit trail is read in. It lives on its own so that the filter on the settings screen and
/// the rows it filters cannot end up naming the same action two different ways.
/// </summary>
public static class AuditText
{
    /// <summary>The action as an operator reads it: a short sentence, not the name of the enum member.</summary>
    public static string Label(AuditAction action) => action switch
    {
        AuditAction.SignInSucceeded => "Signed in",
        AuditAction.SignInFailed => "Sign-in refused",
        AuditAction.SignOut => "Signed out",
        AuditAction.PasswordChanged => "Password changed",
        AuditAction.PasswordReset => "Password reset",
        AuditAction.UserCreated => "Account created",
        AuditAction.UserDeactivated => "Account deactivated",
        AuditAction.UserReactivated => "Account reactivated",
        AuditAction.UserRoleChanged => "Role changed",
        AuditAction.MedicineCreated => "Medicine created",
        AuditAction.MedicineUpdated => "Medicine edited",
        AuditAction.PriceChanged => "Price changed",
        AuditAction.MedicineDeactivated => "Medicine withdrawn",
        AuditAction.MedicineReactivated => "Medicine restored",
        AuditAction.SupplierCreated => "Supplier created",
        AuditAction.SupplierUpdated => "Supplier edited",
        AuditAction.SupplierDeactivated => "Supplier withdrawn",
        AuditAction.SupplierReactivated => "Supplier restored",
        AuditAction.PurchaseCreated => "Supplier invoice recorded",
        AuditAction.PurchaseUpdated => "Supplier invoice edited",
        AuditAction.PurchaseReceived => "Goods received",
        AuditAction.PurchaseReversed => "Receipt reversed",
        AuditAction.PurchaseCancelled => "Invoice cancelled",
        AuditAction.SaleCompleted => "Sale completed",
        AuditAction.SaleCancelled => "Sale cancelled",
        AuditAction.ReturnRecorded => "Return taken",
        AuditAction.StockAdjusted => "Stock adjusted",
        AuditAction.StockQuarantined => "Stock quarantined",
        AuditAction.StockWrittenOff => "Stock written off",
        AuditAction.SupplierReturnRecorded => "Returned to supplier",
        AuditAction.BackupTaken => "Backup taken",
        AuditAction.RestoreCompleted => "Backup restored",
        _ => action.ToString(),
    };
}
