using MediStock.Application.Common;

namespace MediStock.Application.Suppliers;

/// <summary>
/// Records and reads the medicine the pharmacy has sent back to its suppliers for credit.
/// </summary>
/// <remarks>
/// The two kinds of stock that leave the shelf are kept apart: stock a customer brings back is a sale
/// return (a refund against an invoice), stock that goes back up the supply chain is a supplier return
/// (a credit against a purchase). Both are recorded as documents with the stock ledger, so the shelf,
/// the supplier history and the movement ledger can never disagree. See
/// <c>docs/decisions/0011-stock-corrections.md</c>.
/// </remarks>
public interface ISupplierReturnCatalog
{
    /// <summary>
    /// Records one supplier return and applies it: every batch named gives up its quantity in the same
    /// unit of work that writes the document. Medicine can only go back to the supplier it was bought
    /// from, and a return that fails leaves the shelf exactly as it was.
    /// </summary>
    Task<OperationResult<SupplierReturnDto>> RecordAsync(
        SupplierReturnDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>Finds one return by its database identifier, for the detail view.</summary>
    Task<SupplierReturnDto?> FindAsync(int supplierReturnId, CancellationToken cancellationToken = default);

    /// <summary>Lists returns for the supplier-returns screen, newest business date first.</summary>
    Task<IReadOnlyList<SupplierReturnSummaryDto>> SearchAsync(
        SupplierReturnQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The return number the next recorded return will carry, so the screen can show it before the
    /// operator commits anything. Numbers are allocated inside the recording transaction, so two
    /// operators recording at once are sequenced, not duplicated.
    /// </summary>
    Task<string> PreviewReturnNumberAsync(CancellationToken cancellationToken = default);
}