using MediStock.Application.Common;

namespace MediStock.Application.Sales;

/// <summary>
/// Writing side of the point of sale. A basket is priced by <see cref="QuoteAsync"/> and committed by
/// <see cref="CheckoutAsync"/>; both run the same allocation and pricing code, so what the operator
/// was shown is what the customer is charged.
/// </summary>
/// <remarks>
/// Neither method trusts the browser-side totals: a checkout re-reads stock, re-runs first-expiry,
/// first-out and re-computes every amount inside one transaction. The quote is a courtesy; the
/// checkout is the authority.
/// </remarks>
public interface ISaleCatalog
{
    /// <summary>
    /// Prices a basket without changing anything. Returns blockers rather than throwing, because a
    /// half-finished basket is a normal state of the point-of-sale screen, not an error.
    /// </summary>
    Task<SaleQuote> QuoteAsync(SaleDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits the sale: allocates batches, reduces their balances, writes the stock movements and
    /// issues an invoice number, all inside one transaction.
    /// </summary>
    Task<OperationResult<SaleDto>> CheckoutAsync(SaleDraft draft, CancellationToken cancellationToken = default);

    Task<SaleDto?> FindAsync(int saleId, CancellationToken cancellationToken = default);

    Task<SaleDto?> FindByInvoiceNumberAsync(string invoiceNumber, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaleDto>> SearchAsync(SaleQuery query, CancellationToken cancellationToken = default);

    /// <summary>Issues and returns the invoice number the next sale would receive, without consuming it.</summary>
    Task<string> PreviewInvoiceNumberAsync(CancellationToken cancellationToken = default);
}
